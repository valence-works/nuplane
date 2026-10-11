using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Acquires retained native and semantic admission for configured roots and complete install-path sets.</summary>
/// <remarks>This implementation classifies authority but does not itself wire any package-store driver.</remarks>
internal sealed class PackageStoreAdmission : IPackageStoreAdmission
{
    private readonly IPhysicalStoreFileSystem _files;
    private readonly RootMembershipRegistry _registry;
    private readonly PackageStoreAuthorityResolver _resolver;
    private readonly ITrustedPackageStoreRootCatalog? _trustedCatalog;
    private readonly string _configuredRootLocator;
    private readonly string? _configuredRootBaseLocator;

    internal RootMembershipRegistry Registry => _registry;

    internal PackageStoreAdmission(
        IPhysicalStoreFileSystem files,
        RootMembershipRegistry registry,
        string configuredRootLocator,
        string? configuredRootBaseLocator = null,
        ITrustedPackageStoreRootCatalog? trustedCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredRootLocator);
        _files = files;
        _registry = registry;
        _resolver = new PackageStoreAuthorityResolver(files, registry);
        _trustedCatalog = trustedCatalog;
        _configuredRootLocator = configuredRootLocator;
        _configuredRootBaseLocator = configuredRootBaseLocator;
    }

    public async ValueTask<PackageStoreRootOperationAdmission> AcquireConfiguredRootOperationAsync(
        PackageStoreAdmissionKind kind,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        cancellationToken.ThrowIfCancellationRequested();

        var resolved = _trustedCatalog is null
            ? _resolver.Resolve(_configuredRootLocator,
                PhysicalStorePathTarget.ConfiguredRootDirectoryAllowMissingSuffix, exactBaseLocator: _configuredRootBaseLocator)
            : _resolver.ResolveCatalogAdmissionCandidate(_configuredRootLocator,
                PhysicalStorePathTarget.ConfiguredRootDirectoryAllowMissingSuffix, _configuredRootBaseLocator);
        var transferred = false;
        Exception? admissionError = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = resolved.Target as PhysicalStoreDirectoryHandle
                ?? throw Refusal("The configured package-store root is not a held directory.");
            var targetInfo = _files.InspectHandle(target);
            if (targetInfo.Kind != PhysicalStoreEntryKind.Directory)
                throw Refusal("The configured package-store root changed kind during admission.");

            if (resolved.IsProspectiveConfiguredRoot)
            {
                if (resolved.RootIdentity is not null || resolved.MembershipCandidate is not null)
                    throw Refusal("A prospective configured root cannot carry an observed membership authority.");
                resolved.Revalidate();
                return new PackageStoreRootOperationAdmission(PackageStoreAdmissionStatus.Unenrolled,
                    root: null, owner: null);
            }

            if (resolved.RootIdentity is null)
            {
                // The resolver returns no candidate only after observing reserved authority absent at every path edge.
                resolved.Revalidate();
                return new PackageStoreRootOperationAdmission(PackageStoreAdmissionStatus.Unenrolled,
                    new PhysicalRootIdentity(targetInfo.Identity), owner: null);
            }

            var candidate = resolved.MembershipCandidate
                ?? throw Refusal("The configured root has no retained membership candidate.");
            if (_trustedCatalog is not null)
            {
                var session = await AcquireCatalogRuntimeSessionAsync(cancellationToken).ConfigureAwait(false);
                NativeCatalogRuntimeProjection? projection = null;
                try
                {
                    session.RevalidateRequestObservations([resolved]);
                    projection = await _registry.CreateCatalogRuntimeProjectionAsync(session,
                        resolved.RootIdentity!, cancellationToken).ConfigureAwait(false);
                    var retainedProjection = projection
                        ?? throw new InvalidOperationException("Catalog projection creation returned no operation owner.");
                    var validator = new AdmittedPathValidator(_files, _resolver, resolved.RootIdentity!,
                        candidate.EnrollmentEpoch, () => (retainedProjection.Ledger.LedgerDigest, retainedProjection.LedgerIdentity),
                        pathIdentities: null);
                    var state = new PackageStoreOperationState(resolved.RootIdentity!, candidate.EnrollmentEpoch,
                        new EnrolledOperationOwnership(retainedProjection, retainedProjection.LockedMemberLocations, resolved), validator);
                    projection = null;
                    transferred = true;
                    return new PackageStoreRootOperationAdmission(PackageStoreAdmissionStatus.Enrolled,
                        resolved.RootIdentity, state.Owner);
                }
                catch (Exception exception)
                {
                    var cleanupErrors = new List<Exception>();
                    if (projection is not null)
                    {
                        try { await projection.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception cleanupError) { cleanupErrors.Add(cleanupError); }
                    }
                    try { await session.DisposeUnprojectedAsync().ConfigureAwait(false); }
                    catch (Exception cleanupError) { cleanupErrors.Add(cleanupError); }
                    if (cleanupErrors.Count > 0)
                        throw new AggregateException("Catalog-backed configured-root admission failed and its retained session did not release cleanly.",
                            new[] { exception }.Concat(cleanupErrors));
                    throw;
                }
            }

            var ledgerIdentity = resolved.MembershipLedgerIdentity
                ?? throw Refusal("The configured root has no retained ledger-file identity.");
            var owner = await AcquireVerifiedOwnerAsync(resolved.AuthorityRoot!, resolved.RootIdentity,
                candidate.EnrollmentEpoch, candidate.LedgerDigest, ledgerIdentity, cancellationToken).ConfigureAwait(false);
            try
            {
                resolved.Revalidate();
                var validator = new AdmittedPathValidator(_files, _resolver, resolved.RootIdentity,
                    candidate.EnrollmentEpoch, () => (owner.Ledger.LedgerDigest, owner.LedgerIdentity),
                    pathIdentities: null);
                var state = new PackageStoreOperationState(resolved.RootIdentity, candidate.EnrollmentEpoch,
                    new EnrolledOperationOwnership(owner, owner.Context, resolved), validator);
                transferred = true;
                return new PackageStoreRootOperationAdmission(PackageStoreAdmissionStatus.Enrolled,
                    resolved.RootIdentity, state.Owner);
            }
            catch (Exception exception)
            {
                try { await owner.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException(
                        "Configured-root admission failed and its retained owner did not release cleanly.",
                        exception, cleanupError);
                }
                throw;
            }
        }
        catch (Exception exception)
        {
            admissionError = exception;
            throw;
        }
        finally
        {
            if (!transferred)
            {
                if (admissionError is null)
                {
                    resolved.Dispose();
                }
                else
                {
                    try { resolved.Dispose(); }
                    catch (Exception cleanupError)
                    {
                        throw new AggregateException(
                            "Configured-root admission failed and its retained path observation did not close cleanly.",
                            admissionError, cleanupError);
                    }
                }
            }
        }
    }

    public async ValueTask<PackageStorePathAdmission> AcquireForInstallPathsAsync(
        IReadOnlyCollection<string> installPaths,
        PackageStoreAdmissionKind kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installPaths);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        cancellationToken.ThrowIfCancellationRequested();

        var requestedPaths = installPaths.ToArray();
        if (requestedPaths.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Install paths cannot be null, empty, or whitespace.", nameof(installPaths));

        var observations = new List<PathObservation>(requestedPaths.Length);
        var groups = new Dictionary<PhysicalRootIdentity, RootGroup>();
        var owners = new List<RootOwnerEntry>();
        CatalogRuntimeOperationSession? catalogSession = null;
        var transferred = false;
        Exception? admissionError = null;
        try
        {
            foreach (var path in requestedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (resolved, targetKind) = ResolveInstallPath(path);
                PhysicalStoreEntryInfo targetInfo;
                try { targetInfo = _files.InspectHandle(resolved.Target); }
                catch (Exception exception)
                {
                    DisposeResolutionAfterFailure(resolved, exception);
                    throw;
                }
                if ((targetKind == PhysicalStorePathTarget.PackageDirectory && targetInfo.Kind != PhysicalStoreEntryKind.Directory) ||
                    (targetKind == PhysicalStorePathTarget.ArchiveFile &&
                     (targetInfo.Kind != PhysicalStoreEntryKind.RegularFile || targetInfo.LinkCount != 1)))
                {
                    var refusal = Refusal("An install path did not resolve to one supported package directory or archive file.");
                    DisposeResolutionAfterFailure(resolved, refusal);
                    throw refusal;
                }

                var observation = new PathObservation(path, resolved, targetInfo.Identity, targetKind);
                observations.Add(observation);
                if (resolved.RootIdentity is null)
                    continue;

                var candidate = resolved.MembershipCandidate
                    ?? throw Refusal("An enrolled install path has no retained membership candidate.");
                var ledgerIdentity = resolved.MembershipLedgerIdentity
                    ?? throw Refusal("An enrolled install path has no retained ledger-file identity.");
                if (!groups.TryGetValue(resolved.RootIdentity, out var group))
                {
                    group = new RootGroup(resolved.RootIdentity, candidate.EnrollmentEpoch,
                        candidate.LedgerDigest, ledgerIdentity, resolved.AuthorityRoot!);
                    groups.Add(resolved.RootIdentity, group);
                }
                else if (group.Epoch != candidate.EnrollmentEpoch ||
                         !string.Equals(group.LedgerDigest, candidate.LedgerDigest, StringComparison.Ordinal) ||
                         group.LedgerIdentity != ledgerIdentity)
                {
                    throw Refusal("Paths resolving to one physical root observed conflicting membership evidence.", resolved.RootIdentity);
                }

                group.Paths.Add(observation);
            }

            var orderedGroups = groups.Values.OrderBy(static group => group.Root, PhysicalRootIdentityComparer.Instance).ToArray();
            owners.EnsureCapacity(orderedGroups.Length);
            if (_trustedCatalog is not null && orderedGroups.Length > 0)
            {
                catalogSession = await AcquireCatalogRuntimeSessionAsync(cancellationToken).ConfigureAwait(false);
                catalogSession.RevalidateRequestObservations(observations.Select(static observation => observation.Resolved));

                foreach (var group in orderedGroups)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    NativeCatalogRuntimeProjection? projection = null;
                    try
                    {
                        projection = await _registry.CreateCatalogRuntimeProjectionAsync(catalogSession,
                            group.Root, cancellationToken).ConfigureAwait(false);
                        var retainedProjection = projection
                            ?? throw new InvalidOperationException("Catalog projection creation returned no operation owner.");
                        if (retainedProjection.Ledger.EnrollmentEpoch != group.Epoch)
                            throw Refusal("The selected catalog root changed enrollment epoch during request-path recovery.", group.Root);
                        var expectedIdentities = group.Paths
                            .GroupBy(static observation => observation.Path, StringComparer.Ordinal)
                            .ToDictionary(static paths => paths.Key,
                                static paths => new AdmittedPathIdentity(paths.First().TargetIdentity, paths.First().TargetKind),
                                StringComparer.Ordinal);
                        var validator = new AdmittedPathValidator(_files, _resolver, group.Root, group.Epoch,
                            () => (retainedProjection.Ledger.LedgerDigest, retainedProjection.LedgerIdentity), expectedIdentities);
                        var state = new PackageStoreOperationState(group.Root, group.Epoch,
                            new EnrolledOperationOwnership(retainedProjection, retainedProjection.LockedMemberLocations), validator);
                        owners.Add(new RootOwnerEntry(group, retainedProjection));
                        owners[^1].State = state;
                        projection = null;
                    }
                    catch (Exception exception)
                    {
                        if (projection is not null)
                        {
                            try { await projection.DisposeAsync().ConfigureAwait(false); }
                            catch (Exception cleanupError)
                            {
                                throw new AggregateException(
                                    "Catalog-backed path projection failed and its retained context did not release cleanly.",
                                    exception, cleanupError);
                            }
                        }
                        throw;
                    }
                }
            }
            else if (orderedGroups.Length > 1)
            {
                var requests = orderedGroups.Select(static group =>
                    new RootMembershipRegistry.CompleteMemberLocationsRequest(
                        group.RootHandle, group.Root, group.Epoch, group.LedgerDigest, group.LedgerIdentity)).ToArray();
                var unionOwners = await _registry.AcquireCompleteMemberLocationsUnionAsync(requests, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    if (unionOwners.Count != orderedGroups.Length)
                        throw new InvalidOperationException("The complete member-location union returned the wrong root-owner count.");

                    for (var index = 0; index < orderedGroups.Length; index++)
                        owners.Add(new RootOwnerEntry(orderedGroups[index], unionOwners[index]));
                }
                catch (Exception constructionError)
                {
                    var cleanupErrors = new List<Exception>();
                    for (var index = unionOwners.Count - 1; index >= 0; index--)
                    {
                        try { await unionOwners[index].DisposeAsync().ConfigureAwait(false); }
                        catch (Exception exception) { cleanupErrors.Add(exception); }
                    }

                    if (cleanupErrors.Count > 0)
                    {
                        throw new AggregateException(
                            "Multi-root admission owner construction failed and one or more root shares did not release cleanly.",
                            new[] { constructionError }.Concat(cleanupErrors));
                    }

                    throw;
                }
            }
            else if (orderedGroups.Length == 1)
            {
                var group = orderedGroups[0];
                cancellationToken.ThrowIfCancellationRequested();
                var locked = await _registry.AcquireCompleteMemberLocationsAsync(group.RootHandle, group.Root,
                    group.Epoch, group.LedgerDigest, group.LedgerIdentity, cancellationToken).ConfigureAwait(false);
                owners.Add(new RootOwnerEntry(group, locked));
            }

            // The catalog branch already recovered and verified the whole catalog before projecting roots.
            if (catalogSession is null)
            {
                foreach (var owner in owners)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var locked = owner.Locked ?? throw new InvalidOperationException("A root lock scope was not retained.");
                    await _registry.VerifyAllMemberProtectionAsync(owner.Context, owner.Group.RootHandle,
                        owner.Group.Root, owner.Group.Epoch, RootMembershipStatus.Complete, cancellationToken)
                        .ConfigureAwait(false);
                }

                // Retained observations for the complete path union are rechecked after all roots and members verify.
                foreach (var observation in observations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    observation.Resolved.Revalidate();
                }
            }

            foreach (var owner in owners)
            {
                if (owner.State is not null)
                    continue;
                var locked = owner.Locked ?? throw new InvalidOperationException("A root lock scope was not retained.");
                var expectedIdentities = owner.Group.Paths
                    .GroupBy(static observation => observation.Path, StringComparer.Ordinal)
                    .ToDictionary(static group => group.Key,
                        static group => new AdmittedPathIdentity(group.First().TargetIdentity, group.First().TargetKind),
                        StringComparer.Ordinal);
                var validator = new AdmittedPathValidator(_files, _resolver, owner.Group.Root, owner.Group.Epoch,
                    () => (owner.Ledger.LedgerDigest, owner.LedgerIdentity), expectedIdentities);
                var state = new PackageStoreOperationState(owner.Group.Root, owner.Group.Epoch,
                    new EnrolledOperationOwnership(locked, owner.Context), validator);
                owner.State = state;
                owner.Locked = null;
            }

            var ownerByRoot = owners.ToDictionary(static entry => entry.Group.Root, static entry => entry.State!);
            var entries = observations.Select(observation => new PackageStorePathAdmissionEntry(
                observation.Path,
                observation.Resolved.RootIdentity is null ? PackageStoreAdmissionStatus.Unenrolled : PackageStoreAdmissionStatus.Enrolled,
                observation.Resolved.RootIdentity)).ToArray();
            var retainedOwners = owners.Select(static owner => owner.State!).ToArray();
            var pathControl = new PathAdmissionControl(ownerByRoot, retainedOwners, observations);
            var admission = new PackageStorePathAdmission(entries, pathControl);
            pathControl.Bind(admission);
            transferred = true;
            return admission;
        }
        catch (Exception exception)
        {
            admissionError = exception;
            throw;
        }
        finally
        {
            if (!transferred)
            {
                var cleanupErrors = new List<Exception>();
                for (var index = owners.Count - 1; index >= 0; index--)
                {
                    if (owners[index].State is { } state)
                    {
                        try { await state.Owner.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception exception) { cleanupErrors.Add(exception); }
                    }
                    else if (owners[index].Locked is { } locked)
                    {
                        try { await locked.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception exception) { cleanupErrors.Add(exception); }
                    }
                }

                for (var index = observations.Count - 1; index >= 0; index--)
                {
                    try { observations[index].Resolved.Dispose(); }
                    catch (Exception exception) { cleanupErrors.Add(exception); }
                }

                if (catalogSession is not null)
                {
                    try { await catalogSession.DisposeUnprojectedAsync().ConfigureAwait(false); }
                    catch (Exception cleanupError) { cleanupErrors.Add(cleanupError); }
                }

                if (cleanupErrors.Count > 0)
                {
                    var failures = admissionError is null ? cleanupErrors : new[] { admissionError }.Concat(cleanupErrors);
                    throw new AggregateException(
                        "Path admission failed and one or more retained resources did not release cleanly.", failures);
                }

            }
        }
    }

    private async Task<RootMembershipRegistry.CompleteMemberLocationsOwner> AcquireVerifiedOwnerAsync(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity rootIdentity,
        long epoch,
        string ledgerDigest,
        PhysicalFileIdentity ledgerIdentity,
        CancellationToken cancellationToken)
    {
        var locked = await _registry.AcquireCompleteMemberLocationsAsync(root, rootIdentity, epoch,
            ledgerDigest, ledgerIdentity, cancellationToken).ConfigureAwait(false);
        try
        {
            await _registry.VerifyAllMemberProtectionAsync(locked.Context, root, rootIdentity, epoch,
                RootMembershipStatus.Complete, cancellationToken).ConfigureAwait(false);
            return locked;
        }
        catch (Exception exception)
        {
            try { await locked.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "Complete-member verification failed and its retained lock owner did not release cleanly.",
                    exception, cleanupError);
            }
            throw;
        }
    }

    private async Task<CatalogRuntimeOperationSession> AcquireCatalogRuntimeSessionAsync(
        CancellationToken cancellationToken)
    {
        var catalog = _trustedCatalog
            ?? throw new InvalidOperationException("Catalog-backed admission requires the immutable trusted root catalog.");
        var owner = await _registry.AcquireNativeCatalogOwnerAsync(catalog, cancellationToken).ConfigureAwait(false);
        RootMembershipRegistry.NativeCatalogOwnerBorrow? borrow = null;
        CatalogRuntimeOperationSession? session = null;
        try
        {
            borrow = await owner.BorrowAsync(cancellationToken).ConfigureAwait(false);
            session = new CatalogRuntimeOperationSession(_registry, owner, borrow);
            borrow = null;
            await RecoverCatalogPendingAsync(session, cancellationToken).ConfigureAwait(false);
            owner.RevalidateRetainedEvidence();
            _ = await _registry.VerifyCatalogMemberProtectionAsync(session.Borrow, cancellationToken)
                .ConfigureAwait(false);
            RequireSupportedCatalogV1(owner.SnapshotRetainedRoots());
            owner.RevalidateRetainedEvidence();
            return session;
        }
        catch (Exception exception)
        {
            var cleanupErrors = new List<Exception>();
            if (session is not null)
            {
                try { await session.DisposeUnprojectedAsync().ConfigureAwait(false); }
                catch (Exception cleanupError) { cleanupErrors.Add(cleanupError); }
            }
            else
            {
                if (borrow is not null)
                {
                    try { await borrow.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanupError) { cleanupErrors.Add(cleanupError); }
                }
                try { await owner.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanupError) { cleanupErrors.Add(cleanupError); }
            }

            if (cleanupErrors.Count > 0)
                throw new AggregateException("Catalog admission failed and its retained owner did not release cleanly.",
                    new[] { exception }.Concat(cleanupErrors));
            throw;
        }
    }

    private async Task RecoverCatalogPendingAsync(
        CatalogRuntimeOperationSession session,
        CancellationToken cancellationToken)
    {
        var owner = session.Owner;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.RevalidateRetainedEvidence();
            var roots = owner.SnapshotRetainedRoots()
                .OrderBy(static root => root.RootIdentity, PhysicalRootIdentityComparer.Instance)
                .ToArray();
            var localPending = roots.FirstOrDefault(static root => root.Ledger.PendingStateCommit is not null);
            if (localPending is not null)
            {
                await _registry.RecoverLocalAsync(session.Borrow, localPending.RootIdentity, cancellationToken)
                    .ConfigureAwait(false);
                if (session.RequireCurrentRoot(localPending.RootIdentity).Ledger.PendingStateCommit is not null)
                    throw Refusal("Catalog-local recovery did not clear its exact pending transaction.", localPending.RootIdentity);
                continue;
            }

            var pendingDigests = roots
                .Select(static root => root.Ledger.PendingGroupPublicationV2?.Descriptor.IntentDigest)
                .Where(static digest => digest is not null)
                .ToHashSet(StringComparer.Ordinal);
            if (pendingDigests.Count == 0)
                return;

            var descriptor = owner.SnapshotGroupDescriptors()
                .Where(candidate => pendingDigests.Contains(candidate.IntentDigest))
                .OrderBy(static candidate => candidate.IntentDigest, StringComparer.Ordinal)
                .FirstOrDefault()
                ?? throw Refusal("A pending catalog group has no exact retained descriptor.");
            var participants = descriptor.Participants.Select(static participant => participant.RootIdentity).ToArray();
            await _registry.RecoverNativeGroupAsync(session.Borrow, descriptor, cancellationToken)
                .ConfigureAwait(false);
            foreach (var participant in participants)
            {
                var current = session.RequireCurrentRoot(participant);
                if (current.Ledger.PendingGroupPublicationV2?.Descriptor.IntentDigest == descriptor.IntentDigest)
                    throw Refusal("Catalog group recovery did not clear the exact pending descriptor.", participant);
            }
        }
    }

    private static void RequireSupportedCatalogV1(
        IReadOnlyList<NativeCatalogLockedRoot> roots)
    {
        if (roots.Count == 0)
            throw Refusal("Catalog-backed enrolled admission requires at least one enrolled trusted root.");
        foreach (var root in roots)
        {
            var ledger = root.Ledger;
            if (root.ReplayPolicy != RootMembershipRegistry.LocatorReplayBindingPolicy.Acknowledged ||
                ledger.SchemaVersion != RootMembershipRecord.CurrentSchemaVersion ||
                ledger.Status != RootMembershipStatus.Complete || ledger.PendingStateCommit is not null ||
                ledger.PendingGroupPublicationV2 is not null || ledger.Members.Count == 0 ||
                ledger.Members.Any(static member => member.Binding is not RootMemberRecord.AcknowledgedBinding))
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                    "Built-in coordinated runtime admission currently supports only Complete schema-1 acknowledged catalog roots.",
                    root.RootIdentity);
        }
    }

    private static void DisposeResolutionAfterFailure(ResolvedPackageStorePath resolved, Exception admissionError)
    {
        try { resolved.Dispose(); }
        catch (Exception cleanupError)
        {
            throw new AggregateException(
                "Install-path admission failed and its retained path observation did not close cleanly.",
                admissionError, cleanupError);
        }
    }

    private static PackageStoreAdmissionException Refusal(string message, PhysicalRootIdentity? root = null)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message, root);

    private (ResolvedPackageStorePath Resolved, PhysicalStorePathTarget TargetKind) ResolveInstallPath(string path)
    {
        try
        {
            var resolved = _trustedCatalog is null
                ? _resolver.Resolve(path, PhysicalStorePathTarget.PackageDirectory)
                : _resolver.ResolveCatalogAdmissionCandidate(path, PhysicalStorePathTarget.PackageDirectory);
            return (resolved, PhysicalStorePathTarget.PackageDirectory);
        }
        catch (PackageStoreAdmissionException)
        {
            // The second metadata-only walk distinguishes a single-link archive from a directory. Both paths
            // reject final aliases and both must complete before any package/archive payload is opened.
            var resolved = _trustedCatalog is null
                ? _resolver.Resolve(path, PhysicalStorePathTarget.ArchiveFile)
                : _resolver.ResolveCatalogAdmissionCandidate(path, PhysicalStorePathTarget.ArchiveFile);
            return (resolved, PhysicalStorePathTarget.ArchiveFile);
        }
    }

    private sealed class RootGroup(
        PhysicalRootIdentity root,
        long epoch,
        string ledgerDigest,
        PhysicalFileIdentity ledgerIdentity,
        PhysicalStoreDirectoryHandle rootHandle)
    {
        internal PhysicalRootIdentity Root { get; } = root;
        internal long Epoch { get; } = epoch;
        internal string LedgerDigest { get; } = ledgerDigest;
        internal PhysicalFileIdentity LedgerIdentity { get; } = ledgerIdentity;
        internal PhysicalStoreDirectoryHandle RootHandle { get; } = rootHandle;
        internal List<PathObservation> Paths { get; } = [];
    }

    private sealed record PathObservation(
        string Path,
        ResolvedPackageStorePath Resolved,
        PhysicalFileIdentity TargetIdentity,
        PhysicalStorePathTarget TargetKind);

    private sealed record AdmittedPathIdentity(PhysicalFileIdentity Identity, PhysicalStorePathTarget TargetKind);

    private sealed class RootOwnerEntry
    {
        private readonly Func<PhysicalFileIdentity> _getLedgerIdentity;

        internal RootOwnerEntry(RootGroup group, RootMembershipRegistry.CompleteMemberLocationsOwner locked)
            : this(group, locked, locked.Context, () => locked.LedgerIdentity)
        {
        }

        internal RootOwnerEntry(RootGroup group, NativeCatalogRuntimeProjection projection)
            : this(group, projection, projection.LockedMemberLocations, () => projection.LedgerIdentity)
        {
        }

        private RootOwnerEntry(
            RootGroup group,
            IAsyncDisposable locked,
            RootMembershipRegistry.LockedMemberLocations context,
            Func<PhysicalFileIdentity> getLedgerIdentity)
        {
            Group = group;
            Locked = locked;
            Context = context;
            _getLedgerIdentity = getLedgerIdentity;
        }

        internal RootGroup Group { get; }
        internal IAsyncDisposable? Locked { get; set; }
        internal RootMembershipRegistry.LockedMemberLocations Context { get; }
        internal RootMembershipRecord Ledger => Context.Ledger;
        internal PhysicalFileIdentity LedgerIdentity => _getLedgerIdentity();
        internal PackageStoreOperationState? State { get; set; }
    }

    private sealed class EnrolledOperationOwnership : IAsyncDisposable, IStoreOperationLockedMemberContext
    {
        private readonly IAsyncDisposable _locked;
        private readonly RootMembershipRegistry.LockedMemberLocations _context;
        private readonly ResolvedPackageStorePath? _configuredRootObservation;

        internal EnrolledOperationOwnership(
            IAsyncDisposable locked,
            RootMembershipRegistry.LockedMemberLocations context,
            ResolvedPackageStorePath? configuredRootObservation = null)
        {
            ArgumentNullException.ThrowIfNull(locked);
            ArgumentNullException.ThrowIfNull(context);
            _locked = locked;
            _context = context;
            _configuredRootObservation = configuredRootObservation;
        }

        public RootMembershipRegistry.LockedMemberLocations LockedMemberLocations => _context;

        public async ValueTask DisposeAsync()
        {
            var errors = new List<Exception>(2);
            try { await _locked.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { errors.Add(exception); }
            try { _configuredRootObservation?.Dispose(); }
            catch (Exception exception) { errors.Add(exception); }

            if (errors.Count == 1)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
            if (errors.Count > 1)
                throw new AggregateException("Enrolled operation ownership cleanup encountered multiple failures.", errors);
        }
    }

    private sealed class AdmittedPathValidator : IPackageStoreOperationPathValidator,
        IPackageStoreOperationPackageDirectoryValidator,
        IPackageStoreOperationPackageArchiveValidator
    {
        private readonly IPhysicalStoreFileSystem _files;
        private readonly PackageStoreAuthorityResolver _resolver;
        private readonly PhysicalRootIdentity _root;
        private readonly long _epoch;
        private readonly Func<(string Digest, PhysicalFileIdentity Identity)> _ledgerObservation;
        private readonly IReadOnlyDictionary<string, AdmittedPathIdentity>? _pathIdentities;

        internal AdmittedPathValidator(
            IPhysicalStoreFileSystem files,
            PackageStoreAuthorityResolver resolver,
            PhysicalRootIdentity root,
            long epoch,
            Func<(string Digest, PhysicalFileIdentity Identity)> ledgerObservation,
            IReadOnlyDictionary<string, AdmittedPathIdentity>? pathIdentities)
        {
            _files = files;
            _resolver = resolver;
            _root = root;
            _epoch = epoch;
            _ledgerObservation = ledgerObservation;
            _pathIdentities = pathIdentities;
        }

        public void ValidateForInstallPath(string installPath)
        {
            if (_pathIdentities is not null && !_pathIdentities.ContainsKey(installPath))
                throw Refusal("The install path is absent from this operation's admitted path union.", _root);

            var currentLedger = _ledgerObservation();
            var expectedPath = _pathIdentities is not null ? _pathIdentities[installPath] : null;
            var targetKind = expectedPath?.TargetKind ?? PhysicalStorePathTarget.PackageDirectory;
            ResolvedPackageStorePath resolved;
            if (_pathIdentities is null)
            {
                try { resolved = _resolver.Resolve(installPath, targetKind, _root); }
                catch (PackageStoreAdmissionException)
                {
                    targetKind = PhysicalStorePathTarget.ArchiveFile;
                    resolved = _resolver.Resolve(installPath, targetKind, _root);
                }
            }
            else
            {
                resolved = _resolver.Resolve(installPath, targetKind, _root);
            }
            using (resolved)
            {
                ValidateResolvedInstallPath(resolved, targetKind, expectedPath, currentLedger);
                resolved.Revalidate();
            }
        }

        public TResult WithValidatedPackageDirectory<TResult>(
            string installPath,
            Func<IPhysicalStoreFileSystem, PhysicalStoreDirectoryHandle, TResult> callback)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
            ArgumentNullException.ThrowIfNull(callback);
            AdmittedPathIdentity? expectedPath = null;
            if (_pathIdentities is not null)
            {
                if (!_pathIdentities.TryGetValue(installPath, out var admittedPath))
                    throw Refusal("The install path is absent from this operation's admitted path union.", _root);
                expectedPath = admittedPath;
            }

            if (expectedPath is { TargetKind: not PhysicalStorePathTarget.PackageDirectory })
            {
                throw new PackageStoreAdmissionException(
                    PackageStoreAdmissionReason.UnsupportedParticipant,
                    "Scoped package metadata requires an admitted extracted package directory.",
                    _root);
            }

            var currentLedger = _ledgerObservation();
            using var resolved = _resolver.Resolve(installPath, PhysicalStorePathTarget.PackageDirectory, _root);
            ValidateResolvedInstallPath(resolved, PhysicalStorePathTarget.PackageDirectory, expectedPath, currentLedger);
            resolved.Revalidate();

            var directory = resolved.Target as PhysicalStoreDirectoryHandle
                ?? throw Refusal("The admitted package path is not a held directory.", _root);
            TResult result;
            try
            {
                result = callback(_files, directory);
            }
            catch
            {
                // Preserve a refused result if the retained authority/path evidence changed while
                // a callback was failing; otherwise propagate its original read/parse failure.
                resolved.Revalidate();
                throw;
            }
            resolved.Revalidate();
            return result;
        }

        public TResult WithValidatedPackageArchive<TResult>(
            string installPath,
            Func<IPhysicalStoreFileSystem, PhysicalStoreDirectoryHandle, string, PhysicalStoreFileHandle, TResult> callback)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
            ArgumentNullException.ThrowIfNull(callback);
            AdmittedPathIdentity? expectedPath = null;
            if (_pathIdentities is not null)
            {
                if (!_pathIdentities.TryGetValue(installPath, out var admittedPath))
                    throw Refusal("The archive path is absent from this operation's admitted path union.", _root);
                expectedPath = admittedPath;
            }

            if (expectedPath is { TargetKind: not PhysicalStorePathTarget.ArchiveFile })
            {
                throw new PackageStoreAdmissionException(
                    PackageStoreAdmissionReason.UnsupportedParticipant,
                    "Scoped archive reads require the exact admitted archive file.",
                    _root);
            }

            var currentLedger = _ledgerObservation();
            using var resolved = _resolver.Resolve(installPath, PhysicalStorePathTarget.ArchiveFile, _root);
            ValidateResolvedInstallPath(resolved, PhysicalStorePathTarget.ArchiveFile, expectedPath, currentLedger);
            resolved.Revalidate();

            var parent = resolved.TargetParent
                ?? throw Refusal("The admitted archive has no retained native parent directory.", _root);
            var name = resolved.TargetName
                ?? throw Refusal("The admitted archive has no retained native basename.", _root);
            var file = resolved.Target as PhysicalStoreFileHandle
                ?? throw Refusal("The admitted archive target is not a held file.", _root);
            TResult result;
            try
            {
                result = callback(_files, parent, name, file);
            }
            catch
            {
                resolved.Revalidate();
                throw;
            }
            resolved.Revalidate();
            return result;
        }

        public TResult WithValidatedPackageDirectoryOrMissing<TResult>(
            string installPath,
            Func<IPhysicalStoreFileSystem, PhysicalStoreDirectoryHandle?, TResult> callback)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
            ArgumentNullException.ThrowIfNull(callback);
            AdmittedPathIdentity? expectedPath = null;
            if (_pathIdentities is not null)
            {
                if (!_pathIdentities.TryGetValue(installPath, out var admittedPath))
                    throw Refusal("The install path is absent from this operation's admitted path union.", _root);
                expectedPath = admittedPath;
            }

            if (expectedPath is { TargetKind: not PhysicalStorePathTarget.PackageDirectory })
            {
                throw new PackageStoreAdmissionException(
                    PackageStoreAdmissionReason.UnsupportedParticipant,
                    "Scoped package metadata requires an admitted extracted package directory.",
                    _root);
            }

            var currentLedger = _ledgerObservation();
            using var resolved = _resolver.Resolve(
                installPath,
                PhysicalStorePathTarget.AdmittedPackageDirectoryAllowMissingSuffix,
                _root);
            ValidateCompleteOperationRoot(resolved, currentLedger);
            resolved.Revalidate();

            var missing = resolved.IsProspectiveMissingSuffix;
            var directory = missing
                ? null
                : resolved.Target as PhysicalStoreDirectoryHandle
                    ?? throw Refusal("The admitted package path is not a held directory.", _root);
            if (missing)
            {
                // A path-restricted admission captured a concrete directory identity. Its later absence
                // is a changed target, not a cache miss. Only a root-scoped operation can classify an
                // exact missing suffix that was not present when the operation began.
                if (expectedPath is not null)
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                        "The package directory admitted for this operation is now absent.", _root);
                var parent = _files.InspectHandle(resolved.Target);
                if (parent.Kind != PhysicalStoreEntryKind.Directory ||
                    !SameVolume(parent.Identity, _root.HandleIdentity))
                    throw Refusal("The positively absent package path has no same-volume held parent.", _root);
            }
            else
            {
                var target = ValidateResolvedInstallPath(resolved, PhysicalStorePathTarget.PackageDirectory,
                    expectedPath, currentLedger);
                if (directory is null || target.Identity != _files.InspectHandle(directory).Identity)
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                        "The admitted package directory changed during native resolution.", _root);
            }

            TResult result;
            try
            {
                result = callback(_files, directory);
            }
            catch
            {
                resolved.Revalidate();
                ValidateCompleteOperationRoot(resolved, _ledgerObservation());
                throw;
            }

            resolved.Revalidate();
            ValidateCompleteOperationRoot(resolved, _ledgerObservation());
            return result;
        }

        private void ValidateCompleteOperationRoot(
            ResolvedPackageStorePath resolved,
            (string Digest, PhysicalFileIdentity Identity) currentLedger)
        {
            if (resolved.RootIdentity != _root || resolved.MembershipCandidate is not { Status: RootMembershipStatus.Complete } candidate ||
                candidate.EnrollmentEpoch != _epoch ||
                !string.Equals(candidate.LedgerDigest, currentLedger.Digest, StringComparison.Ordinal) ||
                resolved.MembershipLedgerIdentity != currentLedger.Identity)
            {
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                    "The package path no longer matches its live Complete operation owner.", _root);
            }
        }

        private static bool SameVolume(PhysicalFileIdentity left, PhysicalFileIdentity right)
            => string.Equals(left.Provider, right.Provider, StringComparison.Ordinal) &&
                string.Equals(left.VolumeOrDeviceId, right.VolumeOrDeviceId, StringComparison.Ordinal);

        private PhysicalStoreEntryInfo ValidateResolvedInstallPath(
            ResolvedPackageStorePath resolved,
            PhysicalStorePathTarget targetKind,
            AdmittedPathIdentity? expectedPath,
            (string Digest, PhysicalFileIdentity Identity) currentLedger)
        {
            ValidateCompleteOperationRoot(resolved, currentLedger);

            var target = _files.InspectHandle(resolved.Target);
            var expectedKind = targetKind == PhysicalStorePathTarget.ArchiveFile
                ? PhysicalStoreEntryKind.RegularFile : PhysicalStoreEntryKind.Directory;
            if (target.Kind != expectedKind ||
                (targetKind == PhysicalStorePathTarget.ArchiveFile && target.LinkCount != 1) ||
                (expectedPath is not null && target.Identity != expectedPath.Identity))
            {
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                    "The install path no longer identifies the package directory admitted for this operation.", _root);
            }

            return target;
        }
    }

    private sealed class PathAdmissionControl : IPackageStorePathAdmissionControl
    {
        private readonly object _gate = new();
        private readonly IReadOnlyDictionary<PhysicalRootIdentity, PackageStoreOperationState> _owners;
        private readonly IReadOnlyList<PackageStoreOperationState> _retainedOwners;
        private readonly IReadOnlyList<PathObservation> _observations;
        private PackageStorePathAdmission? _admission;
        private bool _closing;
        private Task? _disposeTask;

        internal PathAdmissionControl(
            IReadOnlyDictionary<PhysicalRootIdentity, PackageStoreOperationState> owners,
            IReadOnlyList<PackageStoreOperationState> retainedOwners,
            IReadOnlyList<PathObservation> observations)
        {
            _owners = owners;
            _retainedOwners = retainedOwners;
            _observations = observations;
        }

        internal void Bind(PackageStorePathAdmission admission)
        {
            ArgumentNullException.ThrowIfNull(admission);
            lock (_gate)
            {
                if (_admission is not null)
                    throw new InvalidOperationException("A path admission control can bind only one admission handle.");
                _admission = admission;
            }
        }

        public PackageStoreOperationBorrow BorrowFor(PackageStorePathAdmission admission, string installPath)
        {
            ArgumentNullException.ThrowIfNull(admission);
            ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
            lock (_gate)
            {
                if (!ReferenceEquals(_admission, admission))
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.ExpiredScope,
                        "The admission handle does not own this path-control scope.");
                if (_closing)
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.ExpiredScope,
                        "The path admission is closing.");
                var observation = _observations.FirstOrDefault(item => string.Equals(item.Path, installPath, StringComparison.Ordinal))
                    ?? throw Refusal("The requested path is not part of this admission.");
                var root = observation.Resolved.RootIdentity
                    ?? throw Refusal("An unenrolled path does not receive an enrolled operation borrow.");
                if (!_owners.TryGetValue(root, out var owner))
                    throw Refusal("The path's enrolled root has no retained owner.", root);
                return owner.BorrowForPath(owner.Owner, installPath);
            }
        }

        public ValueTask DisposeAsync(PackageStorePathAdmission admission)
        {
            ArgumentNullException.ThrowIfNull(admission);
            lock (_gate)
            {
                if (!ReferenceEquals(_admission, admission))
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.ExpiredScope,
                        "The admission handle does not own this path-control scope.");
                if (_disposeTask is null)
                {
                    _closing = true;
                    _disposeTask = DisposeCoreAsync();
                }
                return new ValueTask(_disposeTask);
            }
        }

        private async Task DisposeCoreAsync()
        {
            var errors = new List<Exception>();
            for (var index = _retainedOwners.Count - 1; index >= 0; index--)
            {
                try { await _retainedOwners[index].Owner.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { errors.Add(exception); }
            }
            for (var index = _observations.Count - 1; index >= 0; index--)
            {
                try { _observations[index].Resolved.Dispose(); }
                catch (Exception exception) { errors.Add(exception); }
            }
            if (errors.Count == 1)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
            if (errors.Count > 1)
                throw new AggregateException("Path admission cleanup encountered multiple failures.", errors);
        }
    }

}
