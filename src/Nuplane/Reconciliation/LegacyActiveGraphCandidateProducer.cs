using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Feeds.Versioning;
using Nuplane.Operational;
using Nuplane.Reconciliation.Models;
using Nuplane.Reconciliation.PackageFiles;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;

namespace Nuplane.Reconciliation;

/// <summary>Reconstructs one complete legacy Active graph from exact installed packages under bound Incomplete ownership.</summary>
/// <remarks>
/// This producer is an internal offline candidate builder. It performs no acquisition, publication, migration,
/// or admission, and it cannot represent unresolved history as an empty graph. Its disposable result retains the
/// native install and graph-input observations until the caller replays the state and publishes the candidate.
/// </remarks>
internal static class LegacyActiveGraphCandidateProducer
{
    private static readonly NuGetVersionRangeEvaluator VersionRanges = new();

    internal static async Task<LegacyActiveGraphCandidateResult> CreateAsync(
        IPhysicalStoreFileSystem files,
        RootMembershipRegistry registry,
        IStoreStateSerializer serializer,
        RootMembershipRegistry.LockedMemberLocations locked,
        string memberId,
        IReadOnlyList<FeedDefinition> configuredFeeds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(locked);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberId);
        ArgumentNullException.ThrowIfNull(configuredFeeds);
        cancellationToken.ThrowIfCancellationRequested();

        if (!ReferenceEquals(files, registry.Files))
            return LegacyActiveGraphCandidateResult.Unknown("native-provider-mismatch");
        if (serializer is not IPackageProtectionStatePayloadSerializer payloadSerializer)
            return LegacyActiveGraphCandidateResult.Unknown("configured-payload-serializer-unavailable");

        try
        {
            locked.RequirePayloadSerializer(serializer);
            var ledger = locked.Ledger;
            return await locked.WithBoundIncompleteRootAsync(memberId, ledger.RootIdentity, ledger.EnrollmentEpoch,
                (heldFiles, root, legacyState, token) => CreateUnderHeldRootAsync(
                    files, registry, heldFiles, root, ledger, memberId, legacyState, configuredFeeds, payloadSerializer, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PackageStoreAdmissionException exception)
        {
            return LegacyActiveGraphCandidateResult.Unknown($"native-{exception.Reason.ToString().ToLowerInvariant()}");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          FormatException or KeyNotFoundException or OverflowException or
                                          System.Text.Json.JsonException or System.Xml.XmlException or IOException or
                                          NotSupportedException)
        {
            return LegacyActiveGraphCandidateResult.Unknown("legacy-active-graph-inconsistent");
        }
    }

    private static async Task<LegacyActiveGraphCandidateResult> CreateUnderHeldRootAsync(
        IPhysicalStoreFileSystem expectedFiles,
        RootMembershipRegistry registry,
        IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle root,
        RootMembershipRecord ledger,
        string memberId,
        StoreStateRecord? legacyState,
        IReadOnlyList<FeedDefinition> configuredFeeds,
        IPackageProtectionStatePayloadSerializer serializer,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(expectedFiles, files) || legacyState is null ||
            ledger.Status != RootMembershipStatus.Incomplete || ledger.PendingStateCommit is not null ||
            ledger.PendingGroupPublicationV2 is not null || ledger.RootIdentity.HandleIdentity != files.InspectHandle(root).Identity)
            return LegacyActiveGraphCandidateResult.Unknown("bound-incomplete-state-unavailable");

        var member = ledger.Members.SingleOrDefault(candidate =>
            string.Equals(candidate.MemberId, memberId, StringComparison.Ordinal));
        if (member?.Binding is not RootMemberRecord.ExistingUnprotectedBinding binding ||
            !binding.ProtectionMetadataAbsent || legacyState.ProtectionRecord is not null || legacyState.ProtectionBundle is not null ||
            !lockedLocationMatches(memberId, binding, ledger, legacyState))
        {
            return LegacyActiveGraphCandidateResult.Unknown("legacy-state-binding-mismatch");
        }

        var currentStateDigest = ProtectionDigest.StateBody(legacyState);
        if (!string.Equals(binding.StateBodyDigest, currentStateDigest, StringComparison.Ordinal))
            return LegacyActiveGraphCandidateResult.Unknown("legacy-state-body-changed");

        // Detach every nested legacy value through the exact configured participant serializer before using it.
        legacyState = await RoundTripAsync(serializer, legacyState, cancellationToken).ConfigureAwait(false);
        var shape = ValidateLegacyShape(legacyState, configuredFeeds);
        if (shape is null)
            return LegacyActiveGraphCandidateResult.Unknown("legacy-active-graph-unknown");

        var scope = new RootMembershipRegistry.MemberLocatorReplayScope(ledger);
        var bindings = new List<BoundInstall>();
        NativePackageGraphFileReader? graphReader = null;
        try
        {
            var authority = new PackageStoreAuthorityResolver(files, registry);
            var installReader = new PackageInstallIdentityReader(files);
            var installObservations = new Dictionary<string, PackageInstallIdentityReader.PackageInstallIdentityObservation>(
                StringComparer.Ordinal);
            var descriptors = legacyState.ActivePackageDescriptorsByIdNormalized;
            foreach (var (packageId, version) in shape.ActiveVersions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var descriptor = descriptors[packageId];
                var suffix = GetExactInstallSuffix(descriptor);
                var resolved = authority.ResolveProtectedInstallPath(descriptor.InstallPath, scope);
                PackageInstallIdentityReader.PackageInstallIdentityObservation? observed = null;
                try
                {
                    var directory = resolved.Target as PhysicalStoreDirectoryHandle
                        ?? throw Refused("A legacy install descriptor did not resolve to a held package directory.", ledger.RootIdentity);
                    var info = files.InspectHandle(directory);
                    if (resolved.RootIdentity != ledger.RootIdentity || info.Kind != PhysicalStoreEntryKind.Directory)
                        throw Refused("A legacy install descriptor is outside the exact selected native root.", ledger.RootIdentity);

                    observed = installReader.Observe(root, ledger.RootIdentity, suffix, packageId, version);
                    if (observed.InstallIdentity.DirectoryIdentity != info.Identity)
                        throw Refused("The legacy install descriptor and native package-root walk identify different directories.", ledger.RootIdentity);
                    if (!installObservations.TryAdd(descriptor.InstallPath, observed))
                        throw Refused("Two legacy package nodes select the same exact install path.", ledger.RootIdentity);

                    bindings.Add(new BoundInstall(descriptor.InstallPath, descriptor, suffix, resolved, observed, scope));
                    observed = null;
                    resolved = null!;
                }
                finally
                {
                    observed?.Dispose();
                    resolved?.Dispose();
                }
            }

            graphReader = new NativePackageGraphFileReader(files, ledger.RootIdentity, installObservations);
            var resolver = PackageDependencyGraphResolver.CreateOffline(graphReader);
            var packagesById = shape.ActiveVersions.ToDictionary(
                static pair => pair.Key,
                pair => ToResolvedPackage(descriptors[pair.Key], legacyState.UpdatedAt),
                StringComparer.OrdinalIgnoreCase);
            var resolution = await resolver.ResolveAsync(shape.RootRequests,
                (request, token) => Task.FromResult(ResolveRoot(request, packagesById, descriptors, token, ledger.RootIdentity)),
                (request, token) => Task.FromResult(ResolveDependency(request, packagesById, descriptors, token, ledger.RootIdentity)),
                cancellationToken).ConfigureAwait(false);

            if (resolution.ResolvedGraphs.Count != 1 || resolution.HostVersionRefusals.Count != 0 ||
                resolution.UnverifiedHostProvidedDependencies.Count != 0)
                throw Refused("Offline graph reconstruction did not produce one complete supported graph.", ledger.RootIdentity);

            var graph = resolution.ResolvedGraphs[0].CreateImmutableSnapshot();
            ValidateReconstructedGraph(graph, shape, descriptors, ledger.RootIdentity);

            var installs = graph.Nodes.Select(node => bindings.Single(binding =>
                    string.Equals(binding.Descriptor.PackageId, node.PackageId, StringComparison.OrdinalIgnoreCase)))
                .Select(static binding => binding.Observation.InstallIdentity).ToArray();
            var snapshot = RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
                graph, shape.RootRequests, installs, sourceRevision: 1);

            var correlationId = $"legacy-{Guid.NewGuid():N}";
            var activatedAt = DateTimeOffset.UtcNow;
            var records = ActivePackageCatalogMapper.BuildActiveGraphRecords(
                StoreStateRecord.Empty(), [graph], shape.ActiveVersions, correlationId, activatedAt,
                shape.RootRequests.Select(static request => request.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));
            var descriptorsNext = ActivePackageCatalogMapper.BuildNextDescriptors(
                legacyState,
                shape.ActiveVersions,
                resolution.ResolvedPackages,
                new PackageChangeSet(resolution.ResolvedPackages, [], [], correlationId, activatedAt),
                correlationId,
                activatedAt,
                [graph],
                records);

            var closure = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, [snapshot]);
            var next = new StoreStateRecord(
                new Dictionary<string, string>(shape.ActiveVersions, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(legacyState.LastKnownGoodById, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, FailureRecord>(legacyState.LastFailureById, StringComparer.OrdinalIgnoreCase),
                CopySourceSnapshots(legacyState.LastSuccessfulSourceSnapshots),
                activatedAt,
                new Dictionary<string, ActivePackageDescriptor>(descriptorsNext, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, GraphActivationRecord>(records, StringComparer.OrdinalIgnoreCase));
            next = WithInitialProtection(next, ledger.RootIdentity, ledger.EnrollmentEpoch, memberId, closure);
            next = await RoundTripAsync(serializer, next, cancellationToken).ConfigureAwait(false);
            _ = PersistedStoreStateGraphVerifier.Verify(next);

            return LegacyActiveGraphCandidateResult.Verified(
                next,
                graph,
                reader: graphReader,
                bindings,
                scope,
                memberId,
                ledger.RootIdentity,
                ledger.EnrollmentEpoch,
                binding.StateSlot,
                binding.ObservedStateFileIdentity,
                binding.StateBodyDigest);
        }
        catch (Exception primary)
        {
            var cleanupErrors = DisposeBindings(bindings);
            try { scope.Expire(); }
            catch (Exception cleanup) { cleanupErrors.Add(cleanup); }
            if (cleanupErrors.Count != 0)
                throw new AggregateException("Legacy graph reconstruction failed and native observations did not close cleanly.",
                    new[] { primary }.Concat(cleanupErrors));
            throw;
        }

        bool lockedLocationMatches(string selectedMemberId, RootMemberRecord.ExistingUnprotectedBinding selectedBinding,
            RootMembershipRecord selectedLedger, StoreStateRecord selectedState)
        {
            // The caller's LockedMemberLocations already replays this exact slot around the stream read. Here the
            // ledger binding is checked again against the selected member and exact state-body digest.
            return string.Equals(selectedMemberId, memberId, StringComparison.Ordinal) &&
                   selectedLedger.RootIdentity == ledger.RootIdentity &&
                   selectedLedger.EnrollmentEpoch == ledger.EnrollmentEpoch &&
                   string.Equals(selectedBinding.StateBodyDigest, ProtectionDigest.StateBody(selectedState), StringComparison.Ordinal);
        }
    }

    private static LegacyGraphShape? ValidateLegacyShape(
        StoreStateRecord state,
        IReadOnlyList<FeedDefinition> configuredFeeds)
    {
        if (state.ActiveVersionById is null || state.LastKnownGoodById is null || state.LastFailureById is null ||
            state.LastSuccessfulSourceSnapshots is null || state.ActiveVersionById.Count == 0 ||
            state.ActiveGraphsByIdNormalized.Count != 1)
            return null;

        var active = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, version) in state.ActiveVersionById)
        {
            if (string.IsNullOrWhiteSpace(id) || !NuGet.Versioning.NuGetVersion.TryParse(version, out _) || !active.TryAdd(id, version))
                return null;
            if (!state.LastKnownGoodById.TryGetValue(id, out var lkg) || !VersionEquals(version, lkg))
                return null;
        }

        var graphEntry = state.ActiveGraphsByIdNormalized.Single();
        var graph = graphEntry.Value;
        var roots = graph.RootPackageIds?.ToArray() ?? [];
        var nodeIds = graph.NodePackageIds?.ToArray() ?? [];
        var versions = graph.NodeVersionsByPackageId;
        if (!string.Equals(graphEntry.Key, graph.GraphId, StringComparison.OrdinalIgnoreCase) ||
            graph.Status != GraphActivationStatus.Active || string.IsNullOrWhiteSpace(graph.GraphId) ||
            string.IsNullOrWhiteSpace(graph.GenerationId) || roots.Length == 0 || nodeIds.Length == 0 || versions is null ||
            roots.Distinct(StringComparer.OrdinalIgnoreCase).Count() != roots.Length ||
            nodeIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != nodeIds.Length ||
            !new HashSet<string>(nodeIds, StringComparer.OrdinalIgnoreCase).SetEquals(active.Keys) ||
            !new HashSet<string>(versions.Keys, StringComparer.OrdinalIgnoreCase).SetEquals(active.Keys) ||
            active.Any(pair => !versions.TryGetValue(pair.Key, out var version) || !VersionEquals(pair.Value, version)) ||
            roots.Any(root => !active.ContainsKey(root)))
            return null;

        var descriptors = state.ActivePackageDescriptorsByIdNormalized;
        if (!new HashSet<string>(descriptors.Keys, StringComparer.OrdinalIgnoreCase).SetEquals(active.Keys))
            return null;

        var feeds = new Dictionary<string, FeedDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var feed in configuredFeeds)
        {
            if (feed is null || string.IsNullOrWhiteSpace(feed.Name) || feed.ServiceIndex is null ||
                !feed.ServiceIndex.IsAbsoluteUri || feed.ServiceIndex.Scheme is not ("https" or "http") ||
                !feeds.TryAdd(feed.Name, feed))
                return null;
        }

        foreach (var (id, version) in active)
        {
            var descriptor = descriptors[id];
            if (!string.Equals(descriptor.PackageId, id, StringComparison.OrdinalIgnoreCase) ||
                !VersionEquals(descriptor.Version, version) ||
                !string.Equals(descriptor.GraphId, graph.GraphId, StringComparison.Ordinal) ||
                !string.Equals(descriptor.GraphGenerationId, graph.GenerationId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(descriptor.FeedName) || !feeds.ContainsKey(descriptor.FeedName) ||
                string.IsNullOrWhiteSpace(descriptor.SourceName) ||
                descriptor.RootPackageIds is null || !new HashSet<string>(descriptor.RootPackageIds, StringComparer.OrdinalIgnoreCase).SetEquals(roots) ||
                descriptor.DependencyOfPackageIds is null ||
                descriptor.DependencyOfPackageIds.Any(parent => !active.ContainsKey(parent)) ||
                !Enum.IsDefined(descriptor.PackageRole) || string.IsNullOrWhiteSpace(descriptor.InstallPath))
                return null;
            _ = GetExactInstallSuffix(descriptor);
        }

        // Bound this increment to one unambiguous saved root request set. Other request-bearing snapshots
        // could represent a different historical selection and are intentionally left Unknown.
        var requestSnapshots = state.LastSuccessfulSourceSnapshots
            .Where(static pair => pair.Value?.Requests is not null).ToArray();
        if (requestSnapshots.Length != 1)
            return null;
        var (sourceName, sourceSnapshot) = requestSnapshots[0];
        if (string.IsNullOrWhiteSpace(sourceName) || string.IsNullOrWhiteSpace(sourceSnapshot.Version))
            return null;
        var requests = sourceSnapshot.Requests!.ToArray();
        if (requests.Length != roots.Length || requests.Any(static request => request is null) ||
            requests.Select(static request => request.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != requests.Length ||
            !new HashSet<string>(requests.Select(static request => request.Id), StringComparer.OrdinalIgnoreCase).SetEquals(roots))
            return null;

        foreach (var request in requests)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.VersionRange) ||
                string.IsNullOrWhiteSpace(request.SourceName) || !Enum.IsDefined(request.UpdatePolicy) ||
                !string.Equals(request.SourceName, sourceName, StringComparison.Ordinal) ||
                !active.TryGetValue(request.Id, out var version) ||
                !descriptors.TryGetValue(request.Id, out var descriptor) ||
                !string.Equals(request.FeedName, descriptor.FeedName, StringComparison.Ordinal) ||
                !SelectsExactly(request.VersionRange, version))
                return null;
        }

        return new LegacyGraphShape(active, roots, requests);
    }

    private static ResolvedPackage ResolveRoot(
        PackageRequest request,
        IReadOnlyDictionary<string, ResolvedPackage> packages,
        IReadOnlyDictionary<string, ActivePackageDescriptor> descriptors,
        CancellationToken cancellationToken,
        PhysicalRootIdentity root)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!packages.TryGetValue(request.Id, out var package) || !descriptors.TryGetValue(request.Id, out var descriptor) ||
            !string.Equals(request.FeedName, descriptor.FeedName, StringComparison.Ordinal) ||
            !string.Equals(request.SourceName, descriptor.SourceName, StringComparison.Ordinal) ||
            !SelectsExactly(request.VersionRange, package.Version))
            throw Refused("A saved root request does not select its exact active installed descriptor.", root);
        return package;
    }

    private static ResolvedPackage ResolveDependency(
        PackageRequest request,
        IReadOnlyDictionary<string, ResolvedPackage> packages,
        IReadOnlyDictionary<string, ActivePackageDescriptor> descriptors,
        CancellationToken cancellationToken,
        PhysicalRootIdentity root)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!packages.TryGetValue(request.Id, out var package) || !descriptors.TryGetValue(request.Id, out var descriptor) ||
            request.FeedName is not null ||
            !string.Equals(request.SourceName, descriptor.SourceName, StringComparison.Ordinal) ||
            !SelectsExactly(request.VersionRange, package.Version))
            throw Refused("A nuspec dependency has no exact satisfying active installed descriptor.", root);
        return package;
    }

    private static void ValidateReconstructedGraph(
        ResolvedPackageGraph graph,
        LegacyGraphShape shape,
        IReadOnlyDictionary<string, ActivePackageDescriptor> descriptors,
        PhysicalRootIdentity root)
    {
        var nodes = graph.Nodes.ToDictionary(static node => node.PackageId, StringComparer.OrdinalIgnoreCase);
        if (!nodes.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(shape.ActiveVersions.Keys) ||
            !graph.Roots.Select(static node => node.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(shape.RootRequests.Select(static request => request.Id)))
            throw Refused("The reconstructed installed graph does not exactly cover the legacy active nodes and roots.", root);

        var dependents = graph.Edges.GroupBy(static edge => edge.ToPackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key,
                static group => group.Select(edge => edge.FromPackageId).ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        var rootSet = graph.Roots.Select(static node => node.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, version) in shape.ActiveVersions)
        {
            var node = nodes[id];
            var descriptor = descriptors[id];
            var parents = dependents.GetValueOrDefault(id) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var isRoot = rootSet.Contains(id);
            var role = (isRoot, parents.Count > 0) switch
            {
                (true, true) => ActivePackageRole.RootAndDependency,
                (true, false) => ActivePackageRole.Root,
                (false, true) => ActivePackageRole.Dependency,
                _ => throw Refused("The reconstructed graph contains an unrooted active node.", root)
            };

            if (!VersionEquals(node.Version, version) || !string.Equals(node.InstallPath, descriptor.InstallPath, StringComparison.Ordinal) ||
                node.SourceKind != PackageSourceKind.RemoteFeed || !string.Equals(node.SourceName, descriptor.SourceName ?? descriptor.FeedName, StringComparison.Ordinal) ||
                !new HashSet<string>(descriptor.RootPackageIds, StringComparer.OrdinalIgnoreCase).SetEquals(rootSet) ||
                !new HashSet<string>(descriptor.DependencyOfPackageIds, StringComparer.OrdinalIgnoreCase).SetEquals(parents) ||
                descriptor.PackageRole != role || descriptor.Discoverable != (role is not ActivePackageRole.Dependency))
                throw Refused("A reconstructed graph node disagrees with its persisted role, source, path, or reverse-dependency projection.", root);
        }

        foreach (var edge in graph.Edges)
        {
            if (!nodes.TryGetValue(edge.ToPackageId, out var selected) || !VersionEquals(edge.SelectedVersion, selected.Version) ||
                !SelectsExactly(edge.RequestedVersionRange, selected.Version))
                throw Refused("A reconstructed nuspec dependency does not select the exact persisted active version.", root);
        }
    }

    private static string GetExactInstallSuffix(ActivePackageDescriptor descriptor)
    {
        if (!Path.IsPathFullyQualified(descriptor.InstallPath))
            throw Refused("A legacy install path must be fully qualified.");
        var components = descriptor.InstallPath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (components.Length < 3)
            throw Refused("A legacy install path does not contain exact feed, package and version names.");
        var suffix = components[^3..];
        if (!string.Equals(suffix[0], descriptor.FeedName, StringComparison.Ordinal) ||
            !string.Equals(suffix[1], descriptor.PackageId, StringComparison.Ordinal) ||
            !string.Equals(suffix[2], descriptor.Version, StringComparison.Ordinal))
            throw Refused("A legacy install path's exact native name suffix disagrees with its descriptor.");
        _ = PackageInstallIdentityReader.ValidateRelativeInstallPath(string.Join('/', suffix), descriptor.PackageId, descriptor.Version);
        return string.Join('/', suffix);
    }

    private static ResolvedPackage ToResolvedPackage(ActivePackageDescriptor descriptor, DateTimeOffset installedAt) =>
        new(descriptor.PackageId, descriptor.Version, descriptor.FeedName!, descriptor.InstallPath,
            installedAt, descriptor.SourceName ?? string.Empty);

    private static bool SelectsExactly(string range, string version)
    {
        var result = VersionRanges.SelectBestMatch(range, [version]);
        return result.Success && result.SelectedVersion is not null && VersionEquals(result.SelectedVersion, version);
    }

    private static bool VersionEquals(string left, string right) =>
        NuGet.Versioning.NuGetVersion.TryParse(left, out var leftVersion) &&
        NuGet.Versioning.NuGetVersion.TryParse(right, out var rightVersion) &&
        NuGet.Versioning.VersionComparer.VersionRelease.Equals(leftVersion, rightVersion);

    private static Dictionary<string, SourceSnapshotRef> CopySourceSnapshots(
        IReadOnlyDictionary<string, SourceSnapshotRef> snapshots)
    {
        if (snapshots.Any(static pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null ||
                                         pair.Value.Requests?.Any(static request => request is null) == true))
            throw new InvalidOperationException("Legacy source snapshot history is incomplete.");
        return snapshots.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value with
            {
                Requests = pair.Value.Requests is null ? null : Array.AsReadOnly(pair.Value.Requests.Select(static request => request with { }).ToArray())
            },
            StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<StoreStateRecord> RoundTripAsync(
        IPackageProtectionStatePayloadSerializer serializer,
        StoreStateRecord state,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await serializer.WritePayloadAsync(stream, state, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        return await serializer.ReadPayloadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static StoreStateRecord WithInitialProtection(
        StoreStateRecord state,
        PhysicalRootIdentity root,
        long epoch,
        string memberId,
        PackageProtectionClosure closure)
        => PackageStoreProtectionRecordFactory.WithProtection(state, root, epoch, memberId, 1,
            closure, closure);

    private static List<Exception> DisposeBindings(IReadOnlyList<BoundInstall> bindings)
    {
        var failures = new List<Exception>();
        for (var index = bindings.Count - 1; index >= 0; index--)
        {
            try { bindings[index].Dispose(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        return failures;
    }

    private static PackageStoreAdmissionException Refused(string message, PhysicalRootIdentity? root = null) =>
        new(PackageStoreAdmissionReason.UnknownAuthority, message, root);

    private sealed record LegacyGraphShape(
        Dictionary<string, string> ActiveVersions,
        string[] RootIds,
        PackageRequest[] RootRequests);

    internal sealed class BoundInstall : IDisposable
    {
        private readonly ResolvedPackageStorePath _resolved;
        private readonly RootMembershipRegistry.MemberLocatorReplayScope _scope;
        private bool _disposed;

        internal BoundInstall(
            string exactPath,
            ActivePackageDescriptor descriptor,
            string rootRelativePath,
            ResolvedPackageStorePath resolved,
            PackageInstallIdentityReader.PackageInstallIdentityObservation observation,
            RootMembershipRegistry.MemberLocatorReplayScope scope)
        {
            ExactPath = exactPath;
            Descriptor = descriptor;
            RootRelativePath = rootRelativePath;
            _resolved = resolved;
            Observation = observation;
            _scope = scope;
        }

        internal string ExactPath { get; }
        internal ActivePackageDescriptor Descriptor { get; }
        internal string RootRelativePath { get; }
        internal PackageInstallIdentityReader.PackageInstallIdentityObservation Observation { get; }
        internal PhysicalStoreDirectoryHandle InstallDirectory => Observation.InstallDirectory;

        internal void Revalidate()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _scope.EnsureActive();
            _resolved.Revalidate();
            Observation.Revalidate();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Exception? primary = null;
            try { Observation.Dispose(); }
            catch (Exception exception) { primary = exception; }
            try { _resolved.Dispose(); }
            catch (Exception exception)
            {
                if (primary is not null)
                    throw new AggregateException("Legacy native install observations failed to close.", primary, exception);
                throw;
            }
            if (primary is not null)
                throw primary;
        }
    }
}

internal sealed class LegacyActiveGraphCandidateResult : IDisposable
{
    private readonly NativePackageGraphFileReader? _reader;
    private readonly IReadOnlyList<LegacyActiveGraphCandidateProducer.BoundInstall> _bindings;
    private readonly RootMembershipRegistry.MemberLocatorReplayScope? _scope;
    private readonly string? _memberId;
    private readonly PhysicalRootIdentity? _root;
    private readonly long _epoch;
    private readonly StateSlotIdentity? _stateSlot;
    private readonly PhysicalFileIdentity? _stateFileIdentity;
    private readonly string? _sourceBodyDigest;
    private bool _disposed;

    private LegacyActiveGraphCandidateResult(
        string? unknownReasonCode,
        StoreStateRecord? candidate,
        ResolvedPackageGraph? graph,
        NativePackageGraphFileReader? reader,
        IReadOnlyList<LegacyActiveGraphCandidateProducer.BoundInstall>? bindings = null,
        RootMembershipRegistry.MemberLocatorReplayScope? scope = null,
        string? memberId = null,
        PhysicalRootIdentity? root = null,
        long epoch = 0,
        StateSlotIdentity? stateSlot = null,
        PhysicalFileIdentity? stateFileIdentity = null,
        string? sourceBodyDigest = null)
    {
        UnknownReasonCode = unknownReasonCode;
        Candidate = candidate;
        Graph = graph;
        _reader = reader;
        _bindings = bindings ?? [];
        _scope = scope;
        _memberId = memberId;
        _root = root;
        _epoch = epoch;
        _stateSlot = stateSlot;
        _stateFileIdentity = stateFileIdentity;
        _sourceBodyDigest = sourceBodyDigest;
    }

    internal bool IsVerified => Candidate is not null;
    internal string? UnknownReasonCode { get; }
    internal StoreStateRecord? Candidate { get; }
    internal ResolvedPackageGraph? Graph { get; }

    internal static LegacyActiveGraphCandidateResult Unknown(string reasonCode) =>
        new(reasonCode, null, null, null);

    internal static LegacyActiveGraphCandidateResult Verified(
        StoreStateRecord candidate,
        ResolvedPackageGraph graph,
        NativePackageGraphFileReader reader,
        IReadOnlyList<LegacyActiveGraphCandidateProducer.BoundInstall> bindings,
        RootMembershipRegistry.MemberLocatorReplayScope scope,
        string memberId,
        PhysicalRootIdentity root,
        long epoch,
        StateSlotIdentity stateSlot,
        PhysicalFileIdentity stateFileIdentity,
        string sourceBodyDigest) =>
        new(null, candidate, graph, reader, bindings.ToArray(), scope, memberId, root, epoch,
            stateSlot, stateFileIdentity, sourceBodyDigest);

    /// <summary>Replays the exact observed install paths and every graph-input tree before candidate publication.</summary>
    internal void RevalidateNative()
    {
        EnsureVerified();
        _reader!.RevalidateBoundReads();
        foreach (var binding in _bindings)
            binding.Revalidate();
    }

    /// <summary>Replays the original member slot and body before publishing the descriptive candidate.</summary>
    internal async Task RevalidateSourceStateAsync(
        RootMembershipRegistry.LockedMemberLocations locked,
        IStoreStateSerializer serializer,
        CancellationToken cancellationToken)
    {
        EnsureVerified();
        locked.RequirePayloadSerializer(serializer);
        var ledger = locked.Ledger;
        if (ledger.Status != RootMembershipStatus.Incomplete || ledger.PendingStateCommit is not null ||
            ledger.RootIdentity != _root || ledger.EnrollmentEpoch != _epoch ||
            !string.Equals(ledger.LedgerDigest, _scope!.LedgerDigest, StringComparison.Ordinal))
            throw Refused("The legacy state owner changed before graph candidate publication.", _root);

        var member = ledger.Members.SingleOrDefault(candidate =>
            string.Equals(candidate.MemberId, _memberId, StringComparison.Ordinal));
        if (member?.Binding is not RootMemberRecord.ExistingUnprotectedBinding binding ||
            binding.StateSlot != _stateSlot || binding.ObservedStateFileIdentity != _stateFileIdentity ||
            !string.Equals(binding.StateBodyDigest, _sourceBodyDigest, StringComparison.Ordinal))
            throw Refused("The legacy state slot no longer matches the exact captured Incomplete binding.", _root);

        if (!locked.Locations.TryGetValue(_memberId!, out var location) || location.Slot != _stateSlot ||
            location.ExistingFileIdentity != _stateFileIdentity)
            throw Refused("The retained native legacy state-file identity changed before publication.", _root);
        location.Revalidate();
        var latest = await locked.ReadMemberStateAsync(_memberId!, cancellationToken).ConfigureAwait(false)
            ?? throw Refused("The bound legacy state disappeared before candidate publication.", _root);
        if (latest.ProtectionRecord is not null || latest.ProtectionBundle is not null ||
            !string.Equals(ProtectionDigest.StateBody(latest), _sourceBodyDigest, StringComparison.Ordinal))
            throw Refused("The bound legacy state body changed before candidate publication.", _root);
        locked.Revalidate();
        RevalidateNative();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var failures = new List<Exception>();
        for (var index = _bindings.Count - 1; index >= 0; index--)
        {
            try { _bindings[index].Dispose(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        _scope?.Expire();
        if (failures.Count != 0)
            throw new AggregateException("Legacy candidate native install observations did not close cleanly.", failures);
    }

    private void EnsureVerified()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsVerified || _reader is null || _scope is null)
            throw new InvalidOperationException("An Unknown legacy graph result has no publication candidate or native authority.");
        _scope.EnsureActive();
    }

    private static PackageStoreAdmissionException Refused(string message, PhysicalRootIdentity? root) =>
        new(PackageStoreAdmissionReason.UnknownAuthority, message, root);
}
