using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Store.State;
using Nuplane.Registration;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Loading;

/// <summary>
/// The process-wide composition behind <see cref="NuplaneHostIntegratedLoader"/>. It owns exactly the
/// objects a running host's dependency-injection container owns for host-integrated loading — one
/// <see cref="HostIntegratedAssemblyResolutionCatalog"/>, one <see cref="HostIntegratedAssemblyResolver"/>
/// (which installs the <see cref="AssemblyLoadContext.Default"/> resolving hook in its constructor), and
/// <see cref="PackageLoader"/> instances that publish into that one catalog.
/// </summary>
/// <remarks>
/// <para>
/// Three invariants make a host-free load indistinguishable from a host's, and all three are enforced
/// here rather than in the loader, so nothing about the host path changes:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>One resolving hook per process.</b> The composition is created once per process and never
/// disposed, so the resolver — and therefore the <c>Default.Resolving</c> handler — is installed exactly
/// once however often the entry point is called.
/// </description></item>
/// <item><description>
/// <b>One load per package graph per process.</b> Host-integrated contexts are non-collectible, so
/// loading the same graph twice would leave two copies of its assemblies in the process and two
/// disagreeing answers for the same type. Every graph this composition loads is recorded against the
/// loader that owns it; a later call for the same graph reuses that loader's recorded outcome instead of
/// loading again, and a graph that some other Nuplane composition in the process already loaded is
/// refused by <see cref="ForeignGraphLoadGate"/> before any assembly is touched.
/// </description></item>
/// <item><description>
/// <b>Serialized calls.</b> The ownership decision above is check-then-act, so calls are serialized
/// process-wide. A host serializes the equivalent decision through single-flight reconciliation.
/// </description></item>
/// </list>
/// <para>
/// Each call gets its own <see cref="PackageLoader"/>, carrying that call's activation gates, target
/// framework override, and logger, and all of them publish into the single shared catalog. This keeps
/// per-call options honest — no call silently inherits an earlier call's gates — while assembly
/// visibility stays process-global, which is what host-integrated loading means.
/// </para>
/// </remarks>
internal sealed class HostIntegratedLoadComposition
{
    private static readonly SemaphoreSlim CompositionGate = new(1, 1);
    private static readonly AsyncLocal<bool> IsLoadInProgress = new();
    private static HostIntegratedLoadComposition? Current;

    private readonly HostIntegratedAssemblyResolutionCatalog _catalog = new();
    private readonly HostIntegratedAssemblyResolver _resolver;
    private readonly Dictionary<string, GraphOwner> _ownersByGraphKey = new(StringComparer.OrdinalIgnoreCase);

    private HostIntegratedLoadComposition(ILoggerFactory? loggerFactory)
    {
        _resolver = new HostIntegratedAssemblyResolver(
            _catalog,
            loggerFactory?.CreateLogger<HostIntegratedAssemblyResolver>());
        ResolverInstallCount++;
    }

    /// <summary>
    /// Gets how many times this process has installed the host-free <c>Default.Resolving</c> hook. It is
    /// the observable form of the "one resolving hook per process" invariant and must never exceed one.
    /// </summary>
    internal static int ResolverInstallCount { get; private set; }

    /// <summary>
    /// Loads <paramref name="packages"/> into the process-wide composition, creating it on first use.
    /// When <paramref name="state"/> is supplied, graph membership comes from the persisted activation
    /// records in it; otherwise it comes from the graph generation identity on each package.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the calling flow is already inside a load — an activation gate calling back into the
    /// entry point, typically. The call would otherwise wait forever on the process-wide gate this load
    /// already holds.
    /// </exception>
    public static async Task<HostIntegratedLoadResult> LoadAsync(
        IReadOnlyList<ActivePackage> packages,
        StoreStateRecord? state,
        HostIntegratedLoadOptions options,
        CancellationToken cancellationToken)
    {
        if (IsLoadInProgress.Value)
        {
            throw new InvalidOperationException(
                $"A {nameof(NuplaneHostIntegratedLoader)} load is already in progress on this call chain. " +
                $"{nameof(NuplaneHostIntegratedLoader)} must not be called from an {nameof(IPackageActivationGate)}, " +
                "or from anything else a load invokes, because the load holds a process-wide lock for its duration and the nested " +
                "call would wait for it forever. Do the nested work after the outer load returns.");
        }

        await CompositionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IsLoadInProgress.Value = true;

        try
        {
            Current ??= new HostIntegratedLoadComposition(options.LoggerFactory);

            return await Current.LoadCoreAsync(packages, state, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            IsLoadInProgress.Value = false;
            CompositionGate.Release();
        }
    }

    public static async Task<HostIntegratedLoadResult> LoadParticipantAsync(
        NuplaneStoreSnapshotAccess access,
        HostIntegratedLoadOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (IsLoadInProgress.Value)
        {
            throw new InvalidOperationException(
                $"A {nameof(NuplaneHostIntegratedLoader)} load is already in progress on this call chain. " +
                "A nested participant load would wait forever on the process-wide load gate.");
        }

        // Match the ordinary process-composition ordering: acquire the process gate before opening
        // any native snapshot scope, so this flow never waits for that gate while retaining a root.
        await CompositionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IsLoadInProgress.Value = true;
        try
        {
            Current ??= new HostIntegratedLoadComposition(options.LoggerFactory);
            return await Current.LoadParticipantCoreAsync(access, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            IsLoadInProgress.Value = false;
            CompositionGate.Release();
        }
    }

    private async Task<HostIntegratedLoadResult> LoadParticipantCoreAsync(
        NuplaneStoreSnapshotAccess access,
        HostIntegratedLoadOptions options,
        CancellationToken cancellationToken)
    {
        AdmittedActivePackageSnapshot? snapshot = null;
        var acquiredOwners = new List<PackageGraphUseLeaseOwner>();
        var handedToLoader = false;
        Exception? primaryFailure = null;
        try
        {
            snapshot = await access.OpenActivePackageSnapshotForLoadingAsync(cancellationToken).ConfigureAwait(false);
            var selections = ParticipantActiveGraphSelectionBuilder.Build(snapshot.Snapshot, snapshot.State);
            var packages = snapshot.Snapshot.Packages;
            if (packages.Count == 0)
            {
                await snapshot.DisposeAsync().ConfigureAwait(false);
                snapshot = null;
                return new HostIntegratedLoadResult([], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            }

            var targetFramework = NormalizeTargetFramework(options.TargetFrameworkOverride);
            var prepared = selections.Select(selection =>
            {
                var key = PackageLoader.BuildGraphKey(selection.Packages);
                return (Selection: selection, Key: key);
            }).ToArray();
            GuardAgainstTargetFrameworkChange(prepared.Select(static entry => entry.Key), targetFramework);

            var ownerByPackageKey = new Dictionary<string, GraphOwner>(StringComparer.OrdinalIgnoreCase);
            var toLoad = new List<LeasedProtectedActiveGraphSelection>();
            foreach (var (selection, key) in prepared)
            {
                if (_ownersByGraphKey.TryGetValue(key, out var existingOwner))
                {
                    ValidateExistingParticipantOwner(existingOwner, key, selection, access.Composition);
                    AssignOwner(ownerByPackageKey, selection.Packages, existingOwner);
                    continue;
                }

                var firstPath = selection.Packages[0].InstallPath;
                using var borrow = snapshot.BorrowRootFor(firstPath);
                var leaseOwner = await access.AcquireProtectedActiveGraphAsync(
                    borrow, selection.Graph, selection.ExactPathsByNode, cancellationToken).ConfigureAwait(false);
                acquiredOwners.Add(leaseOwner);
                toLoad.Add(new LeasedProtectedActiveGraphSelection(
                    selection.Graph, selection.Packages, selection.ExactPathsByNode, leaseOwner));
            }

            // The published graph-use owners now protect the package bytes. Release the short
            // complete-catalog snapshot before metadata, activation callbacks, or assembly reads.
            await snapshot.DisposeAsync().ConfigureAwait(false);
            snapshot = null;

            if (toLoad.Count > 0)
            {
                var loader = CreateLoader(options, targetFramework, access.Composition);
                var graphOwner = new GraphOwner(loader, new AssemblyScanCandidateProjector(loader), targetFramework,
                    ParticipantComposition: access.Composition,
                    ParticipantGraphs: toLoad.ToDictionary(
                        selection => PackageLoader.BuildGraphKey(selection.Packages),
                        selection => new ParticipantProtectedGraphBinding(
                            selection.Graph,
                            selection.ExactPathsByNode,
                            selection.LeaseOwner.Lease),
                        StringComparer.OrdinalIgnoreCase));
                var graphKeys = new List<string>(toLoad.Count);
                foreach (var selection in toLoad)
                {
                    var key = PackageLoader.BuildGraphKey(selection.Packages);
                    _ownersByGraphKey.Add(key, graphOwner);
                    graphKeys.Add(key);
                    AssignOwner(ownerByPackageKey, selection.Packages, graphOwner);
                }

                var sharedPolicy = options.SharedAssemblies
                    .Select(static identity => new SharedAssemblyPolicyEntry(identity.Name, identity.PublicKeyToken, identity.MajorVersion))
                    .ToArray();
                try
                {
                    handedToLoader = true;
                    await ((IScopedPackageLoader)loader).EnsureProtectedActiveGraphsLoadedAsync(
                        toLoad, sharedPolicy, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    ReleaseGraphsThatLoadedNothing(graphKeys);
                }
            }

            return Project(packages, ownerByPackageKey);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            if (snapshot is not null)
            {
                try { await snapshot.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanupFailure) { cleanupFailures.Add(cleanupFailure); }
            }

            if (!handedToLoader)
            {
                for (var index = acquiredOwners.Count - 1; index >= 0; index--)
                {
                    try { await acquiredOwners[index].DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanupFailure) { cleanupFailures.Add(cleanupFailure); }
                }
            }

            if (cleanupFailures.Count > 0)
            {
                if (primaryFailure is not null)
                    cleanupFailures.Insert(0, primaryFailure);
                throw new AggregateException(
                    "Participant loading failed or completed and one or more admitted owners did not close cleanly.",
                    cleanupFailures);
            }
        }
    }

    private async Task<HostIntegratedLoadResult> LoadCoreAsync(
        IReadOnlyList<ActivePackage> packages,
        StoreStateRecord? state,
        HostIntegratedLoadOptions options,
        CancellationToken cancellationToken)
    {
        var resolvedPackages = packages.Select(ToResolvedPackage).ToArray();
        var graphs = state is null
            ? PackageGraphGrouping.ByGraphGeneration(
                resolvedPackages,
                BuildGraphGenerationSelector(packages))
            : PackageGraphGrouping.ForActiveState(resolvedPackages, state);

        var targetFramework = NormalizeTargetFramework(options.TargetFrameworkOverride);
        var graphsByKey = graphs
            .Select(graph => (Key: PackageLoader.BuildGraphKey(graph), Graph: graph))
            .ToArray();

        // Checked before anything is claimed, so a rejected call leaves the process exactly as it found it.
        GuardAgainstTargetFrameworkChange(graphsByKey.Select(static entry => entry.Key), targetFramework);

        var loader = CreateLoader(options, targetFramework);
        var owner = new GraphOwner(loader, new AssemblyScanCandidateProjector(loader), targetFramework);
        var ownerByPackageKey = new Dictionary<string, GraphOwner>(StringComparer.OrdinalIgnoreCase);
        var graphsToLoad = new List<IReadOnlyList<ResolvedPackage>>(graphs.Count);
        var dispatchedGraphKeys = new List<string>(graphs.Count);

        foreach (var (graphKey, graph) in graphsByKey)
        {
            if (_ownersByGraphKey.TryGetValue(graphKey, out var existingOwner))
            {
                // Already loaded by an earlier call in this process. Report what that load produced
                // instead of loading the same assemblies into a second non-collectible context.
                AssignOwner(ownerByPackageKey, graph, existingOwner);
                continue;
            }

            _ownersByGraphKey[graphKey] = owner;
            dispatchedGraphKeys.Add(graphKey);
            AssignOwner(ownerByPackageKey, graph, owner);
            graphsToLoad.Add(graph);
        }

        if (graphsToLoad.Count > 0)
        {
            var sharedPolicy = options.SharedAssemblies
                .Select(static identity => new SharedAssemblyPolicyEntry(identity.Name, identity.PublicKeyToken, identity.MajorVersion))
                .ToArray();

            try
            {
                await loader.EnsureGraphLoadedAsync(graphsToLoad, sharedPolicy, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Runs on cancellation too: a graph that was claimed but never actually loaded must not
                // stay claimed, or a retry would be answered from an outcome that never happened.
                ReleaseGraphsThatLoadedNothing(dispatchedGraphKeys);
            }
        }

        return Project(packages, ownerByPackageKey);
    }

    /// <summary>
    /// Builds the loader a running host would build for host-integrated loading: the same metadata
    /// load-mode advisor the loading module registers by default, the shared resolution catalog and
    /// resolver, and this call's gates and target framework.
    /// </summary>
    /// <remarks>
    /// The load mode is not forced per package. The real selection pipeline runs with
    /// <see cref="PackageLoadMode.HostIntegrated"/> as the configured default, which is what a host
    /// configured for host-integrated loading does: package metadata asking for
    /// <see cref="PackageLoadMode.HostIntegrated"/> is honoured, metadata asking for
    /// <see cref="PackageLoadMode.Collectible"/> is suppressed by the configured default exactly as it
    /// is for such a host, and the load-mode diagnostics a caller reads are the ones the host would
    /// report. Every graph therefore loads host-integrated, which is the purpose of this entry point.
    /// </remarks>
    private PackageLoader CreateLoader(
        HostIntegratedLoadOptions options,
        string? targetFramework,
        PackageStoreCompositionSnapshot? composition = null)
    {
        var loggerFactory = options.LoggerFactory;
        var loadingOptions = new LoadingOptions
        {
            Enabled = true,
            DefaultLoadMode = PackageLoadMode.HostIntegrated,
            LoadModeSelectionPolicy = PackageLoadModeSelectionPolicy.Automatic
        };

        return new PackageLoader(
            hostIntegratedResolutionCatalog: _catalog,
            loadModeSelector: new PackageLoadModeSelector(
                [
                    new PackageMetadataLoadModeAdvisor(
                        new PackageMetadataLoadModeReader(composition is null
                            ? null
                            : new Nuplane.Metadata.NuplanePackageMetadataReader(composition)),
                        loggerFactory?.CreateLogger<PackageMetadataLoadModeAdvisor>())
                ],
                loggerFactory?.CreateLogger<PackageLoadModeSelector>(), composition: composition),
            options: Options.Create(loadingOptions),
            logger: loggerFactory?.CreateLogger<PackageLoader>(),
            hostIntegratedAssemblyResolver: _resolver,
            activationGates: [new ForeignGraphLoadGate(), .. options.ActivationGates],
            hostTargetFrameworkOverride: targetFramework,
            composition: composition);
    }

    /// <summary>
    /// Refuses to answer a call that asks for a graph this process already loaded, but for a different
    /// target framework. The load is irreversible, so the requested assets can never be the ones in the
    /// process; reporting the already-loaded ones as this call's outcome would silently answer a
    /// question the caller did not ask.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the target framework of a previously loaded graph differs from this call's.</exception>
    private void GuardAgainstTargetFrameworkChange(IEnumerable<string> graphKeys, string? targetFramework)
    {
        foreach (var graphKey in graphKeys)
        {
            if (_ownersByGraphKey.TryGetValue(graphKey, out var existingOwner)
                && !string.Equals(existingOwner.TargetFramework, targetFramework, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Package graph '{graphKey}' is already loaded into this process for target framework " +
                    $"'{DescribeTargetFramework(existingOwner.TargetFramework)}', but this call asks for " +
                    $"'{DescribeTargetFramework(targetFramework)}'. A host-integrated load cannot be undone, so a package " +
                    "graph is loaded at most once per process; load the other target framework's assets in a separate process.");
            }
        }
    }

    private static string? NormalizeTargetFramework(string? targetFrameworkOverride) =>
        string.IsNullOrWhiteSpace(targetFrameworkOverride) ? null : targetFrameworkOverride.Trim();

    private static string DescribeTargetFramework(string? targetFramework) =>
        targetFramework ?? "the current process";

    private void ReleaseGraphsThatLoadedNothing(IReadOnlyList<string> dispatchedGraphKeys)
    {
        foreach (var graphKey in dispatchedGraphKeys.Where(static graphKey => !HasLoadContext(graphKey)))
        {
            // Nothing was loaded for this graph — an activation gate refused it, its install paths could
            // not be resolved, or every member was inert — so no assembly of it is in the process and a
            // later call may attempt it again, exactly as a host's next reconcile would.
            _ownersByGraphKey.Remove(graphKey);
        }
    }

    private static HostIntegratedLoadResult Project(
        IReadOnlyList<ActivePackage> packages,
        IReadOnlyDictionary<string, GraphOwner> ownerByPackageKey)
    {
        var states = packages
            .Select(package =>
            {
                var owner = ownerByPackageKey[BuildPackageKey(package.PackageId, package.Version)];
                return PackageLoadStateProjection.Project(package, owner.Loader, owner.Projector);
            })
            .ToArray();

        return new(
            states,
            states
                .Where(static state => state.Status == PackageLoadStatus.Failed)
                .ToDictionary(
                    static state => state.PackageId,
                    static state => state.Diagnostics.Count > 0 ? state.Diagnostics[0] : "loading-failed",
                    StringComparer.OrdinalIgnoreCase));
    }

    private static void AssignOwner(
        Dictionary<string, GraphOwner> ownerByPackageKey,
        IReadOnlyList<ResolvedPackage> graph,
        GraphOwner owner)
    {
        foreach (var package in graph)
        {
            ownerByPackageKey[BuildPackageKey(package.Id, package.Version)] = owner;
        }
    }

    /// <summary>
    /// Reports the graph generation identity of each package for a caller that supplied only the active
    /// package set. It is the same identity the store's active package descriptors carry, because that
    /// is where an <see cref="ActivePackage"/> gets it.
    /// </summary>
    private static Func<ResolvedPackage, string> BuildGraphGenerationSelector(IReadOnlyList<ActivePackage> packages)
    {
        var graphGenerationByPackageId = packages.ToDictionary(
            static package => package.PackageId,
            static package => package.GraphGenerationId,
            StringComparer.OrdinalIgnoreCase);

        return package => graphGenerationByPackageId[package.Id];
    }

    private static ResolvedPackage ToResolvedPackage(ActivePackage package) =>
        new(
            package.PackageId,
            package.Version,
            package.FeedName ?? string.Empty,
            package.InstallPath,
            package.ActivatedAtUtc,
            package.SourceName ?? string.Empty);

    private static string BuildPackageKey(string packageId, string version) => $"{packageId}@{version}";

    /// <summary>
    /// Determines whether this process already holds a non-collectible load context for the graph. The
    /// context is named with the graph key, which is derived only from the graph's package identities,
    /// so this recognises the same package set no matter which composition loaded it.
    /// </summary>
    private static bool HasLoadContext(string graphKey) =>
        AssemblyLoadContext.All.Any(context =>
            !context.IsCollectible && string.Equals(context.Name, graphKey, StringComparison.Ordinal));

    private static void ValidateExistingParticipantOwner(
        GraphOwner owner,
        string graphKey,
        ParticipantActiveGraphSelectionBuilder.Selection selection,
        PackageStoreCompositionSnapshot composition)
    {
        if (owner.ParticipantComposition is null || owner.ParticipantGraphs is null ||
            !SameComposition(owner.ParticipantComposition, composition) ||
            !owner.ParticipantGraphs.TryGetValue(graphKey, out var binding) ||
            !SameGraph(binding.Graph, selection.Graph) ||
            binding.ExactPathsByNode.Count != selection.ExactPathsByNode.Count ||
            selection.ExactPathsByNode.Any(pair =>
                !binding.ExactPathsByNode.TryGetValue(pair.Key, out var path) ||
                !string.Equals(path, pair.Value, StringComparison.Ordinal)))
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                $"Package graph '{graphKey}' was loaded under a different participant composition or persisted graph identity.");
        }

        AssemblyLoadContext? retainedContext = null;
        foreach (var package in selection.Packages)
        {
            if (!owner.Loader.TryGetContext(package.Id, package.Version, out var contextHandle) ||
                contextHandle?.Context is not AssemblyLoadContext context || context.IsCollectible ||
                context is not IGraphUseLeaseBoundLoadContext leaseBound ||
                !ReferenceEquals(leaseBound.GraphUseLease, binding.Lease))
            {
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                    $"Package graph '{graphKey}' no longer has the exact retained participant load context and graph-use lease.");
            }

            if (retainedContext is not null && !ReferenceEquals(retainedContext, context))
            {
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                    $"Package graph '{graphKey}' is split across multiple process-retained load contexts.");
            }

            retainedContext = context;
            var node = selection.Graph.Nodes.SingleOrDefault(node =>
                string.Equals(node.Install.PackageId, package.Id, StringComparison.OrdinalIgnoreCase));
            if (node is null ||
                !string.Equals(package.PackageContentHash, node.Install.VerifiedArchiveHash, StringComparison.Ordinal) ||
                !string.Equals(binding.ExactPathsByNode[node.NodeId], package.InstallPath, StringComparison.Ordinal) ||
                binding.Lease.GetInstallIdentityForExactPath(package.InstallPath) != node.Install)
            {
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                    $"Package graph '{graphKey}' no longer matches its retained exact install-path lease.");
            }
        }

        if (retainedContext is null)
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.ExpiredScope,
                $"Package graph '{graphKey}' has no retained process load context.");
        }
    }

    private static bool SameComposition(
        PackageStoreCompositionSnapshot left,
        PackageStoreCompositionSnapshot right)
    {
        if (left.Mode != right.Mode || left.AllClientsParticipate != right.AllClientsParticipate ||
            left.Roots.Count != right.Roots.Count || left.Members.Count != right.Members.Count)
            return false;

        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return left.Roots.All(existing => right.Roots.Any(candidate =>
                   string.Equals(existing.Label, candidate.Label, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(existing.RootLocator, candidate.RootLocator, pathComparison))) &&
               left.Members.All(existing => right.Members.Any(candidate =>
                   string.Equals(existing.RootLabel, candidate.RootLabel, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(existing.MemberId, candidate.MemberId, StringComparison.Ordinal) &&
                   string.Equals(existing.StateFileLocator, candidate.StateFileLocator, pathComparison)));
    }

    private static bool SameGraph(ProtectedGraphSnapshot left, ProtectedGraphSnapshot right) =>
        left.SnapshotId == right.SnapshotId &&
        string.Equals(left.GraphId, right.GraphId, StringComparison.Ordinal) &&
        string.Equals(left.GenerationId, right.GenerationId, StringComparison.Ordinal) &&
        left.Disposition == right.Disposition &&
        left.Roots.SequenceEqual(right.Roots) &&
        left.RequestedRoots.SequenceEqual(right.RequestedRoots) &&
        left.Nodes.SequenceEqual(right.Nodes) &&
        left.Edges.SequenceEqual(right.Edges);

    private sealed record ParticipantProtectedGraphBinding(
        ProtectedGraphSnapshot Graph,
        IReadOnlyDictionary<Guid, string> ExactPathsByNode,
        PackageGraphUseLease Lease);

    private sealed record GraphOwner(
        PackageLoader Loader,
        AssemblyScanCandidateProjector Projector,
        string? TargetFramework,
        PackageStoreCompositionSnapshot? ParticipantComposition = null,
        IReadOnlyDictionary<string, ParticipantProtectedGraphBinding>? ParticipantGraphs = null);

    /// <summary>
    /// Refuses a graph that another Nuplane composition in this process — a composed host, most likely —
    /// has already loaded host-integrated. Loading it again would put a second copy of every one of its
    /// assemblies in the process, and by-name resolution would then answer with whichever copy the
    /// first matching <c>Default.Resolving</c> handler returns. Because it is an activation gate, the
    /// refusal is recorded through the loader's ordinary failure bookkeeping.
    /// </summary>
    /// <remarks>
    /// Graphs this composition already owns never reach the gate, so an existing context at this point
    /// is always a foreign one.
    /// </remarks>
    private sealed class ForeignGraphLoadGate : IPackagePathIndependentActivationGate
    {
        public ValueTask<PackageActivationGateResult> EvaluateAsync(
            PackageActivationContext context,
            CancellationToken cancellationToken) =>
            new(HasLoadContext(context.GraphKey)
                ? PackageActivationGateResult.Block(
                    "this package graph is already loaded into a non-collectible assembly load context by another Nuplane composition in this process; " +
                    "loading it again would create a second copy of its assemblies. Use the assemblies that composition already loaded, " +
                    "or run the host-free load in a process that does not compose a Nuplane host.")
                : PackageActivationGateResult.Allow);
    }
}
