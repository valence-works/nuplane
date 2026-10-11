using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;

namespace Nuplane.Store.Coordination;

internal sealed partial class RootMembershipRegistry
{
    private readonly object _catalogOwnerMint = new();

    internal Task<NativeCatalogRuntimeProjection> CreateCatalogRuntimeProjectionAsync(
        CatalogRuntimeOperationSession session,
        PhysicalRootIdentity rootIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(session.Registry, this))
            throw Refused("A catalog runtime session minted by another registry cannot project an operation.", rootIdentity);
        return session.Owner.CreateRuntimeProjectionAsync(session.Borrow, rootIdentity, session,
            cancellationToken, _catalogOwnerMint);
    }

    /// <summary>Resolves and retains every immutable configured root, then locks the complete enrolled catalog.</summary>
    internal Task<NativeCatalogOwner> AcquireNativeCatalogOwnerAsync(
        ITrustedPackageStoreRootCatalog catalog,
        CancellationToken cancellationToken)
        => AcquireNativeCatalogOwnerCoreAsync(catalog, null, cancellationToken);

    /// <summary>Acquires the catalog against one immutable descriptor for an initial group publication.</summary>
    internal Task<NativeCatalogOwner> AcquireNativeCatalogOwnerAsync(
        ITrustedPackageStoreRootCatalog catalog,
        GroupPublicationDescriptorV2 descriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return AcquireNativeCatalogOwnerCoreAsync(catalog, descriptor, cancellationToken);
    }

    private async Task<NativeCatalogOwner> AcquireNativeCatalogOwnerCoreAsync(
        ITrustedPackageStoreRootCatalog catalog,
        GroupPublicationDescriptorV2? plannedDescriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        cancellationToken.ThrowIfCancellationRequested();
        var catalogRootList = catalog.Roots
            ?? throw Refused("The trusted package-store catalog did not provide an immutable root list.");
        var configuredRoots = catalogRootList.ToArray();
        if (configuredRoots.Any(static root => root is null || string.IsNullOrWhiteSpace(root.Label) ||
                string.IsNullOrWhiteSpace(root.RootPath)))
            throw Refused("The trusted package-store catalog contains a blank root locator.");

        var resolver = new PackageStoreAuthorityResolver(_files, this);
        var observations = new List<NativeCatalogLocatorObservation>(configuredRoots.Length);
        NativeCatalogLockedRoot[] enrolledObservations = [];
        PhysicalStoreLock.PhysicalStoreLockUnionOwner? union = null;
        var transferred = false;
        try
        {
            // Resolve and retain every configured locator before deciding which roots are enrolled.
            foreach (var configured in configuredRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolved = resolver.ResolveTrustedCatalogRoot(configured.RootPath);
                try
                {
                    if (resolved.MembershipCandidate is null &&
                        (resolved.AuthorityRoot is not null || resolved.RootIdentity is not null))
                        throw Refused("A configured root has incomplete authority evidence instead of positive absence.");
                    resolved.Revalidate();
                    observations.Add(new NativeCatalogLocatorObservation(configured, resolved));
                }
                catch
                {
                    resolved.Dispose();
                    throw;
                }
            }

            enrolledObservations = observations.Where(static item => item.Resolution.MembershipCandidate is not null)
                .GroupBy(static item => item.Resolution.RootIdentity!, EqualityComparer<PhysicalRootIdentity>.Default)
                .Select(group =>
                {
                    var aliases = group.ToArray();
                    var first = aliases[0].Resolution;
                    if (aliases.Any(alias => alias.Resolution.RootIdentity != first.RootIdentity ||
                            alias.Resolution.MembershipCandidate!.EnrollmentEpoch != first.MembershipCandidate!.EnrollmentEpoch ||
                            alias.Resolution.MembershipCandidate.LedgerDigest != first.MembershipCandidate.LedgerDigest ||
                            alias.Resolution.MembershipLedgerIdentity != first.MembershipLedgerIdentity))
                        throw Refused("Configured aliases for one physical root retain different membership evidence.", first.RootIdentity!);
                    return new NativeCatalogLockedRoot(aliases, first.RootIdentity!,
                        first.MembershipCandidate!.EnrollmentEpoch, first.MembershipCandidate.LedgerDigest,
                        first.MembershipLedgerIdentity!);
                })
                .OrderBy(static root => root.RootIdentity, PhysicalRootIdentityComparer.Instance)
                .ToArray();

            var groupDescriptors = observations
                .Select(static item => item.Resolution.MembershipCandidate?.PendingGroupPublicationV2?.Descriptor)
                .Where(static descriptor => descriptor is not null)
                .Cast<GroupPublicationDescriptorV2>()
                .Concat(plannedDescriptor is null ? [] : [plannedDescriptor])
                .DistinctBy(static descriptor => descriptor.IntentDigest)
                .ToArray();

            // Acquire every canonical root lock before deriving even the first member-lock obligation.
            foreach (var root in enrolledObservations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rootHandle = root.PrimaryResolution.AuthorityRoot
                    ?? throw Refused("An enrolled catalog root has no retained native root handle.", root.RootIdentity);
                RequireRoot(rootHandle, root.RootIdentity);
                var control = OpenControl(rootHandle);
                PhysicalStoreLock.RootLockScope? rootScope = null;
                try
                {
                    var initial = ReadLedger(rootHandle, control, out var identity);
                    var policy = RequireCatalogLedgerShape(initial, root.RootIdentity, root.EnrollmentEpoch, groupDescriptors);
                    RequireExpectedLedgerObservation(initial, identity, root.LedgerDigest, root.ResolvedLedgerIdentity);
                    rootScope = await _locks.AcquireRootLockAsync(control, root.RootIdentity, cancellationToken)
                        .ConfigureAwait(false);
                    control = null!;
                    root.RootScope = rootScope;
                    root.ControlDirectory = rootScope.ControlDirectory;
                    root.InitialLedger = initial;
                    root.InitialLedgerIdentity = identity;
                    root.ReplayPolicy = policy;
                    rootScope = null;
                }
                finally
                {
                    if (rootScope is not null)
                        await rootScope.DisposeAsync().ConfigureAwait(false);
                    else if (control is not null)
                        control.Dispose();
                }
            }

            foreach (var root in enrolledObservations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                root.RootScope!.VerifyCanonical();
                var locked = ReadLedger(root.PrimaryResolution.AuthorityRoot!, root.ControlDirectory!, out var identity);
                root.ReplayPolicy = RequireCatalogLedgerShape(locked, root.RootIdentity, root.EnrollmentEpoch, groupDescriptors);
                RequireExpectedLedgerObservation(locked, identity, root.LedgerDigest, root.ResolvedLedgerIdentity);
                RequireSameDigest(root.InitialLedger!, locked);
                if (root.InitialLedgerIdentity != identity)
                    throw Refused("A catalog membership ledger identity changed during canonical root-lock acquisition.", root.RootIdentity);
                root.LockedLedger = locked;
                root.LockedLedgerIdentity = identity;
                root.LockedSlots = locked.Members.Select(GetSlot).ToArray();
            }

            if (enrolledObservations.Length > 0)
            {
                union = await _locks.AcquireMemberLockUnionAsync(
                    enrolledObservations.Select(static root => root.RootScope!).ToArray(),
                    enrolledObservations.Select(static root => new PhysicalStoreLock.RootMemberLockRequest(
                        root.RootScope!, root.LockedSlots!)).ToArray(), cancellationToken).ConfigureAwait(false);
                foreach (var root in enrolledObservations)
                    root.RootScope = null; // The physical union consumed the scopes and now owns every lock/control handle.

                foreach (var root in enrolledObservations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var current = ReadLedger(root.PrimaryResolution.AuthorityRoot!, root.ControlDirectory!, out var identity);
                    root.ReplayPolicy = RequireCatalogLedgerShape(current, root.RootIdentity, root.EnrollmentEpoch, groupDescriptors);
                    RequireExpectedLedgerObservation(current, identity, root.LedgerDigest, root.ResolvedLedgerIdentity);
                    RequireSameDigest(root.LockedLedger!, current);
                    if (root.LockedLedgerIdentity != identity)
                        throw Refused("A catalog membership ledger changed after the full root/member lock union was acquired.", root.RootIdentity);
                    root.LockedLedger = current;
                    root.LockedLedgerIdentity = identity;
                    root.Scope = new MemberLocatorReplayScope(current,
                        root.ReplayPolicy == LocatorReplayBindingPolicy.PendingStateRecovery);
                    root.Locations = ResolveMemberLocatorMap(current, root.Scope, root.ReplayPolicy);
                }

            }

            RequireCatalogGroupDescriptors(enrolledObservations, groupDescriptors);

            foreach (var observation in observations)
                observation.Resolution.Revalidate();
            var owner = new NativeCatalogOwner(this, _catalogOwnerMint, catalog, catalogRootList, configuredRoots,
                resolver, observations, enrolledObservations, union, groupDescriptors);
            transferred = true;
            return owner;
        }
        finally
        {
            if (!transferred)
            {
                var errors = new List<Exception>();
                foreach (var root in enrolledObservations)
                {
                    if (root.Locations is not null)
                    {
                        try { DisposeLocations(root.Locations.Values); } catch (Exception exception) { errors.Add(exception); }
                    }
                    root.Scope?.Expire();
                }
                if (union is not null)
                {
                    try { await union.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { errors.Add(exception); }
                }
                else
                {
                    for (var index = enrolledObservations.Length - 1; index >= 0; index--)
                    {
                        if (enrolledObservations[index].RootScope is not { } scope)
                            continue;
                        try { await scope.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { errors.Add(exception); }
                    }
                }
                foreach (var observation in observations.AsEnumerable().Reverse())
                {
                    try { observation.Resolution.Dispose(); } catch (Exception exception) { errors.Add(exception); }
                }
                if (errors.Count > 0)
                    throw new AggregateException("Catalog owner acquisition failed and retained native evidence did not fully release.", errors);
            }
        }
    }

    private static LocatorReplayBindingPolicy RequireCatalogLedgerShape(
        RootMembershipRecord ledger, PhysicalRootIdentity rootIdentity, long enrollmentEpoch,
        IReadOnlyList<GroupPublicationDescriptorV2>? groupDescriptors = null)
    {
        if (ledger.RootIdentity != rootIdentity || ledger.EnrollmentEpoch != enrollmentEpoch || ledger.Members.Count == 0)
            throw Refused("A configured catalog root does not retain its exact enrolled root and epoch.", rootIdentity);
        var memberIds = ledger.Members.Select(static member => member.MemberId).ToHashSet(StringComparer.Ordinal);
        if (memberIds.Count != ledger.Members.Count || !memberIds.SetEquals(ledger.TargetMemberIds))
            throw Refused("A catalog owner requires the exact non-empty member and target union.", rootIdentity);

        if (ledger.PendingStateCommit is not null)
        {
            RequireRecoverablePendingStateLocatorLedger(ledger);
            return LocatorReplayBindingPolicy.PendingStateRecovery;
        }

        if (ledger.Status == RootMembershipStatus.Complete && ledger.PendingGroupPublicationV2 is null)
        {
            if (ledger.Members.Any(static member => member.Binding is not (RootMemberRecord.AcknowledgedBinding or
                    RootMemberRecord.BundleAcknowledgedBinding or RootMemberRecord.ExistingUnprotectedBinding)))
                throw Refused("A Complete catalog ledger contains an unrecognized or unbound member.", rootIdentity);
            if (ledger.RetiredMembers.Any(retired => memberIds.Contains(retired.MemberId)))
                throw Refused("A Complete catalog ledger has conflicting retired-member evidence.", rootIdentity);
            return ledger.Members.All(static member => member.Binding is RootMemberRecord.AcknowledgedBinding)
                ? LocatorReplayBindingPolicy.Acknowledged
                : LocatorReplayBindingPolicy.GroupAcknowledged;
        }

        var groupPending = ledger.PendingGroupPublicationV2;
        if (ledger.Status == RootMembershipStatus.Incomplete && groupPending is not null &&
            groupPending.LocalParticipant.RootIdentity == rootIdentity &&
            groupPending.LocalParticipant.EnrollmentEpoch == enrollmentEpoch &&
            ledger.Members.All(static member => member.Binding is (RootMemberRecord.ProspectiveBinding or
                RootMemberRecord.AcknowledgedBinding or RootMemberRecord.BundleAcknowledgedBinding or
                RootMemberRecord.ExistingUnprotectedBinding)))
        {
            var selected = ledger.Members.SingleOrDefault(member =>
                member.MemberId == groupPending.LocalParticipant.PriorMember.MemberId);
            if (selected is null || !RootMembershipRecord.BindingsEqualForGroup(selected.Binding,
                    groupPending.LocalParticipant.PriorMember.Binding))
                throw Refused("A group Intent does not retain its exact local prior-member binding.", rootIdentity);
            return LocatorReplayBindingPolicy.GroupAcknowledged;
        }

        var matchingDescriptors = (groupDescriptors ?? Array.Empty<GroupPublicationDescriptorV2>())
            .SelectMany(descriptor => descriptor.Participants
                .Where(participant => participant.RootIdentity == rootIdentity)
                .Select(participant => (Descriptor: descriptor, Participant: participant)))
            .ToArray();
        if (ledger.Status == RootMembershipStatus.Incomplete && groupPending is null && matchingDescriptors.Length == 1)
        {
            var (descriptor, participant) = matchingDescriptors[0];
            RequireGroupLedgerForDescriptor(ledger, participant, descriptor);
            return LocatorReplayBindingPolicy.GroupAcknowledged;
        }

        throw Refused("A catalog owner refuses generic Incomplete, declared, malformed, or unsupported membership evidence.", rootIdentity);
    }

    private static void RequireCatalogGroupDescriptors(
        IReadOnlyList<NativeCatalogLockedRoot> roots,
        IReadOnlyList<GroupPublicationDescriptorV2> descriptors)
    {
        var byIdentity = roots.ToDictionary(static root => root.RootIdentity);
        foreach (var descriptor in descriptors)
        {
            RequireCatalogDescriptorClosure(roots, descriptor);
            foreach (var participant in descriptor.Participants)
            {
                if (!byIdentity.TryGetValue(participant.RootIdentity, out var root))
                    throw Refused("A pending group descriptor names a participant outside the positively enrolled catalog.",
                        participant.RootIdentity);
                if (root.EnrollmentEpoch != participant.EnrollmentEpoch)
                    throw Refused("A pending group descriptor names a stale catalog participant epoch.", participant.RootIdentity);
                RequireGroupLedgerForDescriptor(root.Ledger, participant, descriptor);
            }
        }
    }

    private static void RequireCatalogDescriptorClosure(
        IReadOnlyList<NativeCatalogLockedRoot> roots,
        GroupPublicationDescriptorV2 descriptor)
    {
        var actual = roots.SelectMany(root => root.Ledger.Members
                .Where(member => GetSlot(member) == descriptor.SharedStateSlot)
                .Select(member => (root.RootIdentity, root.EnrollmentEpoch, member.MemberId)))
            .OrderBy(static item => item.RootIdentity, PhysicalRootIdentityComparer.Instance)
            .ThenBy(static item => item.MemberId, StringComparer.Ordinal)
            .ToArray();
        var expected = descriptor.Participants
            .Select(static participant => (participant.RootIdentity, participant.EnrollmentEpoch,
                participant.PriorMember.MemberId))
            .OrderBy(static item => item.RootIdentity, PhysicalRootIdentityComparer.Instance)
            .ThenBy(static item => item.MemberId, StringComparer.Ordinal)
            .ToArray();
        if (!actual.SequenceEqual(expected))
            throw Refused("A group descriptor must cover every and only retained catalog member bound to its shared state slot.");
    }

    internal sealed class NativeCatalogOwner : IAsyncDisposable
    {
        private readonly RootMembershipRegistry _registry;
        private readonly ITrustedPackageStoreRootCatalog _catalog;
        private readonly IReadOnlyList<TrustedPackageStoreRoot> _catalogRootList;
        private readonly TrustedPackageStoreRoot[] _configuredRoots;
        private readonly PackageStoreAuthorityResolver _resolver;
        private readonly List<GroupPublicationDescriptorV2> _groupDescriptors;
        private readonly SemaphoreSlim _operationGate = new(1, 1);
        private readonly object _gate = new();
        private readonly List<NativeCatalogLocatorObservation> _observations;
        private Dictionary<PhysicalRootIdentity, NativeCatalogLockedRoot> _roots;
        private PhysicalStoreLock.PhysicalStoreLockUnionOwner? _union;
        private TaskCompletionSource? _drained;
        private int _borrowRequests;
        private bool _closing;
        private bool _poisoned;
        private Task? _disposeTask;

        internal NativeCatalogOwner(RootMembershipRegistry registry, object mint, ITrustedPackageStoreRootCatalog catalog,
            IReadOnlyList<TrustedPackageStoreRoot> catalogRootList, TrustedPackageStoreRoot[] configuredRoots,
            PackageStoreAuthorityResolver resolver,
            List<NativeCatalogLocatorObservation> observations, IReadOnlyList<NativeCatalogLockedRoot> roots,
            PhysicalStoreLock.PhysicalStoreLockUnionOwner? union,
            IReadOnlyList<GroupPublicationDescriptorV2> groupDescriptors)
        {
            if (!ReferenceEquals(mint, registry._catalogOwnerMint))
                throw new InvalidOperationException("Only the registry can mint a native catalog owner.");
            _registry = registry;
            _catalog = catalog;
            _catalogRootList = catalogRootList;
            _configuredRoots = configuredRoots;
            _resolver = resolver;
            _observations = observations;
            _roots = roots.ToDictionary(static root => root.RootIdentity);
            _union = union;
            _groupDescriptors = groupDescriptors.Select(static descriptor => descriptor.Copy()).ToList();
        }

        internal async Task<NativeCatalogOwnerBorrow> BorrowAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closing || _poisoned, this);
                _borrowRequests++;
            }

            try
            {
                await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                ReleaseBorrowRequest();
                throw;
            }

            lock (_gate)
            {
                if (_closing || _poisoned)
                {
                    _operationGate.Release();
                    ReleaseBorrowRequestLocked();
                    throw new ObjectDisposedException(nameof(NativeCatalogOwner));
                }
            }

            try
            {
                RevalidateRetainedEvidence();
                return new NativeCatalogOwnerBorrow(this, _registry, _registry._catalogOwnerMint);
            }
            catch
            {
                _operationGate.Release();
                ReleaseBorrowRequest();
                throw;
            }
        }

        internal NativeCatalogLockedRoot RequireRoot(PhysicalRootIdentity rootIdentity, object mint)
        {
            if (!ReferenceEquals(mint, _registry._catalogOwnerMint))
                throw Refused("Only the minting registry can inspect native catalog lock evidence.", rootIdentity);
            ArgumentNullException.ThrowIfNull(rootIdentity);
            if (!_roots.TryGetValue(rootIdentity, out var root))
                throw Refused("The requested participant is absent from the positively enrolled native catalog.", rootIdentity);
            return root;
        }

        internal NativeCatalogLockedRoot[] SnapshotRetainedRoots() => _roots.Values.ToArray();

        internal GroupPublicationDescriptorV2[] SnapshotGroupDescriptors()
            => _groupDescriptors.Select(static descriptor => descriptor.Copy()).ToArray();

        internal Task RefreshRuntimeProjectionAfterOwnedOutcomeAsync(
            NativeCatalogOwnerBorrow borrow,
            PhysicalRootIdentity rootIdentity,
            RootMembershipRecord ledger,
            PhysicalFileIdentity ledgerIdentity)
        {
            ArgumentNullException.ThrowIfNull(borrow);
            ArgumentNullException.ThrowIfNull(rootIdentity);
            ArgumentNullException.ThrowIfNull(ledger);
            ArgumentNullException.ThrowIfNull(ledgerIdentity);
            borrow.RequireRegistry(_registry);
            if (!ReferenceEquals(borrow.Owner, this) || ledger.RootIdentity != rootIdentity)
                throw Refused("A runtime projection outcome does not belong to this retained catalog session.", rootIdentity);
            return RefreshAfterOwnedOutcomesAsync(
                new Dictionary<PhysicalRootIdentity, (RootMembershipRecord Ledger, PhysicalFileIdentity Identity)>
                {
                    [rootIdentity] = (ledger, ledgerIdentity)
                },
                adoptedDescriptor: null,
                _registry._catalogOwnerMint);
        }

        internal void RequireOperationMayStart()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closing, this);
                if (_poisoned)
                    throw Refused("A failed adopted operation poisoned this catalog owner; acquire a fresh owner after recovery review.");
            }
        }

        internal IAsyncDisposable CreateShare()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_union is null || _borrowRequests == 0, this);
                return _union.CreateShare();
            }
        }

        internal void RevalidateRetainedEvidence()
        {
            if (!ReferenceEquals(_catalog.Roots, _catalogRootList) || !_catalog.Roots.SequenceEqual(_configuredRoots))
                throw Refused("The trusted configured-root catalog changed after native ownership was minted.");
            foreach (var observation in _observations)
                observation.Resolution.Revalidate();
            foreach (var root in _roots.Values)
            {
                var control = _registry.OpenControl(root.PrimaryResolution.AuthorityRoot!);
                try
                {
                    var current = _registry.ReadLedger(root.PrimaryResolution.AuthorityRoot!, control, out var identity);
                    var policy = RequireCatalogLedgerShape(current, root.RootIdentity, root.EnrollmentEpoch, _groupDescriptors);
                    RequireExpectedLedgerObservation(current, identity, root.Ledger.LedgerDigest, root.LedgerIdentity);
                    RequireSameDigest(root.Ledger, current);
                    if (identity != root.LedgerIdentity || policy != root.ReplayPolicy)
                        throw Refused("A retained catalog root ledger identity or recovery class changed.", root.RootIdentity);
                    root.Scope!.RequireCandidate(current.RootIdentity, current);
                    RevalidateMemberLocationMap(current, root.Locations!, root.ReplayPolicy);
                }
                finally { control.Dispose(); }
            }
        }

        private async Task<Transaction> OpenTransactionAsync(NativeCatalogLockedRoot root)
        {
            var rootHandle = root.PrimaryResolution.AuthorityRoot
                ?? throw Refused("An enrolled catalog root lost its retained native handle.", root.RootIdentity);
            var share = CreateShare();
            PhysicalStoreDirectoryHandle? control = null;
            try
            {
                control = _registry.OpenControl(rootHandle);
                var ledger = _registry.ReadLedger(rootHandle, control, out var identity, root.LedgerIdentity);
                RequireCatalogLedgerShape(ledger, root.RootIdentity, root.EnrollmentEpoch, _groupDescriptors);
                RequireSameDigest(root.Ledger, ledger);
                if (identity != root.LedgerIdentity)
                    throw Refused("A catalog root changed before its typed adopted operation began.", root.RootIdentity);
                var transaction = new Transaction(_registry, rootHandle, control, share, ledger, identity);
                control = null;
                share = null!;
                return transaction;
            }
            finally
            {
                control?.Dispose();
                if (share is not null)
                    await share.DisposeAsync().ConfigureAwait(false);
            }
        }

        internal async Task<NativeCatalogRuntimeProjection> CreateRuntimeProjectionAsync(
            NativeCatalogOwnerBorrow borrow,
            PhysicalRootIdentity rootIdentity,
            CatalogRuntimeOperationSession session,
            CancellationToken cancellationToken,
            object mint)
        {
            if (!ReferenceEquals(mint, _registry._catalogOwnerMint) || !ReferenceEquals(borrow.Owner, this))
                throw Refused("Only this registry-minted catalog borrow can project a runtime operation.", rootIdentity);
            borrow.RequireRegistry(_registry);
            ArgumentNullException.ThrowIfNull(session);
            cancellationToken.ThrowIfCancellationRequested();
            RevalidateRetainedEvidence();
            var root = RequireRoot(rootIdentity, mint);
            if (root.ReplayPolicy != LocatorReplayBindingPolicy.Acknowledged ||
                root.Ledger.SchemaVersion != RootMembershipRecord.CurrentSchemaVersion ||
                root.Ledger.Status != RootMembershipStatus.Complete || root.Ledger.PendingStateCommit is not null ||
                root.Ledger.PendingGroupPublicationV2 is not null ||
                root.Ledger.Members.Any(static member => member.Binding is not RootMemberRecord.AcknowledgedBinding))
                throw Refused("Runtime projection requires a verified Complete schema-1 acknowledged catalog root.", rootIdentity);

            var rootHandle = root.PrimaryResolution.AuthorityRoot
                ?? throw Refused("The selected catalog root has no retained native directory.", rootIdentity);
            Transaction? transaction = null;
            MemberLocatorReplayScope? scope = null;
            IReadOnlyDictionary<string, ResolvedMemberStateLocation>? locations = null;
            LockedMemberLocations? context = null;
            IAsyncDisposable? projectionShare = null;
            try
            {
                var projectedTransaction = await OpenTransactionAsync(root).ConfigureAwait(false);
                transaction = projectedTransaction;
                var ledger = projectedTransaction.ReadCurrent();
                if (ledger.SchemaVersion != RootMembershipRecord.CurrentSchemaVersion ||
                    ledger.Status != RootMembershipStatus.Complete || ledger.PendingStateCommit is not null ||
                    ledger.PendingGroupPublicationV2 is not null ||
                    ledger.Members.Any(static member => member.Binding is not RootMemberRecord.AcknowledgedBinding))
                    throw Refused("The selected catalog root changed out of Complete schema-1 acknowledged state before projection.", rootIdentity);

                scope = new MemberLocatorReplayScope(ledger);
                locations = _registry.ResolveMemberLocatorMap(ledger, scope, LocatorReplayBindingPolicy.Acknowledged);
                var slots = ledger.Members.Select(static member => RootMembershipRegistry.GetSlot(member)).ToArray();
                if (root.LockedSlots is null || !slots.SequenceEqual(root.LockedSlots))
                    throw Refused("The projected member map differs from the catalog's retained root-local lock obligations.", rootIdentity);
                RevalidateMemberLocationMap(ledger, locations, LocatorReplayBindingPolicy.Acknowledged);
                context = _registry.CreateLockedMemberLocations(projectedTransaction, rootHandle, scope, locations,
                    LocatorReplayBindingPolicy.Acknowledged, rootIdentity, root.EnrollmentEpoch,
                    session,
                    published => session.RefreshAfterOwnedPublicationAsync(
                        rootIdentity, published, projectedTransaction.LedgerIdentity));
                scope = null;
                locations = null;
                RevalidateRetainedEvidence();
                projectionShare = session.CreateProjectionShare();
                var projection = new NativeCatalogRuntimeProjection(context, () => projectedTransaction.LedgerIdentity,
                    () => projectedTransaction.DisposeAsync(),
                    session,
                    projectionShare);
                context = null;
                transaction = null;
                projectionShare = null;
                return projection;
            }
            catch (Exception exception)
            {
                var cleanupErrors = new List<Exception>();
                try { context?.Dispose(); }
                catch (Exception cleanupError) { cleanupErrors.Add(cleanupError); }
                if (context is null && locations is not null)
                {
                    try { DisposeLocations(locations.Values); }
                    catch (Exception cleanupError) { cleanupErrors.Add(cleanupError); }
                }
                try { scope?.Expire(); }
                catch (Exception cleanupError) { cleanupErrors.Add(cleanupError); }
                if (transaction is not null)
                {
                    try { await transaction.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanupError) { cleanupErrors.Add(cleanupError); }
                }
                if (projectionShare is not null)
                {
                    try { await projectionShare.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanupError) { cleanupErrors.Add(cleanupError); }
                }
                if (cleanupErrors.Count > 0)
                    throw new AggregateException("Catalog runtime projection failed and retained resources did not release cleanly.",
                        new[] { exception }.Concat(cleanupErrors));
                throw;
            }
        }

        internal async Task<RootMembershipRecord> RecoverLocalAsync(
            NativeCatalogOwnerBorrow borrow,
            PhysicalRootIdentity rootIdentity,
            CancellationToken cancellationToken,
            Action<RootMembershipPublicationPoint>? checkpoint,
            object mint)
        {
            if (!ReferenceEquals(mint, _registry._catalogOwnerMint) || !ReferenceEquals(borrow.Owner, this))
                throw Refused("Only this registry-minted catalog borrow can adopt local recovery.");
            borrow.RequireRegistry(_registry);
            using var operation = borrow.EnterOperation(_registry);
            cancellationToken.ThrowIfCancellationRequested();
            RevalidateRetainedEvidence();
            var root = RequireRoot(rootIdentity, mint);
            if (root.ReplayPolicy != LocatorReplayBindingPolicy.PendingStateRecovery)
                throw Refused("Typed local adoption requires the exact retained PendingState recovery class.", rootIdentity);
            await using var transaction = await OpenTransactionAsync(root).ConfigureAwait(false);
            try
            {
                var parents = root.Locations!.ToDictionary(static pair => pair.Key,
                    static pair => pair.Value.Parent, StringComparer.Ordinal);
                var recovered = await _registry.RecoverAsync(transaction, parents, cancellationToken, checkpoint)
                    .ConfigureAwait(false);
                var ledger = transaction.ReadCurrent();
                await RefreshAfterOwnedOutcomesAsync(new Dictionary<PhysicalRootIdentity,
                    (RootMembershipRecord Ledger, PhysicalFileIdentity Identity)>
                    { [rootIdentity] = (ledger, transaction.LedgerIdentity) }, null, mint).ConfigureAwait(false);
                return recovered;
            }
            catch
            {
                Poison();
                throw;
            }
        }

        internal async Task RefreshAfterOwnedOutcomesAsync(
            IReadOnlyDictionary<PhysicalRootIdentity, (RootMembershipRecord Ledger, PhysicalFileIdentity Identity)> outcomes,
            GroupPublicationDescriptorV2? adoptedDescriptor,
            object mint)
        {
            if (!ReferenceEquals(mint, _registry._catalogOwnerMint))
                throw Refused("Only the minting registry can refresh owned catalog ledger evidence.");
            if (!ReferenceEquals(_catalog.Roots, _catalogRootList) || !_catalog.Roots.SequenceEqual(_configuredRoots))
                throw Refused("The trusted configured-root catalog changed during an adopted operation.");
            var refreshed = new List<NativeCatalogLocatorObservation>(_observations.Count);
            Dictionary<PhysicalRootIdentity, NativeCatalogLockedRoot>? replacements = null;
            try
            {
                foreach (var priorObservation in _observations)
                {
                    var prior = priorObservation.Resolution;
                    if (prior.MembershipCandidate is null)
                        prior.Revalidate(); // A formerly absent control namespace must remain positively absent.
                    else
                    {
                        var retainedRoot = RequireRoot(prior.RootIdentity!, _registry._catalogOwnerMint);
                        var expected = outcomes.TryGetValue(retainedRoot.RootIdentity, out var outcome)
                            ? outcome
                            : (retainedRoot.Ledger, retainedRoot.LedgerIdentity);
                        prior.RevalidateOwnedLedgerOutcome(expected.Item1, expected.Item2);
                    }
                    var resolution = _resolver.ResolveTrustedCatalogRoot(priorObservation.Configured.RootPath);
                    try
                    {
                        resolution.Revalidate();
                        if ((prior.MembershipCandidate is null) != (resolution.MembershipCandidate is null) ||
                            prior.RootIdentity != resolution.RootIdentity)
                            throw Refused("A configured catalog locator changed its retained enrollment authority.");
                        if (prior.MembershipCandidate is null)
                        {
                            if (resolution.RootIdentity is not null)
                                throw Refused("An absent configured root gained control authority during an owned operation.");
                        }
                        else
                        {
                            var rootIdentity = prior.RootIdentity!;
                            var root = RequireRoot(rootIdentity, _registry._catalogOwnerMint);
                            var candidate = resolution.MembershipCandidate!;
                            if (candidate.RootIdentity != root.RootIdentity || candidate.EnrollmentEpoch != root.EnrollmentEpoch)
                                throw Refused("An owned transaction cannot refresh a different physical root or enrollment epoch.", rootIdentity);
                            var expected = outcomes.TryGetValue(rootIdentity, out var outcome)
                                ? outcome
                                : (root.Ledger, root.LedgerIdentity);
                            if (candidate.LedgerDigest != expected.Item1.LedgerDigest ||
                                resolution.MembershipLedgerIdentity != expected.Item2)
                                throw Refused("The refreshed catalog ledger is not the exact retained transaction outcome.", rootIdentity);
                        }
                        if (prior.MembershipCandidate is null)
                            prior.Revalidate();
                        else
                        {
                            var retainedRoot = RequireRoot(prior.RootIdentity!, _registry._catalogOwnerMint);
                            var expected = outcomes.TryGetValue(retainedRoot.RootIdentity, out var outcome)
                                ? outcome
                                : (retainedRoot.Ledger, retainedRoot.LedgerIdentity);
                            prior.RevalidateOwnedLedgerOutcome(expected.Item1, expected.Item2);
                        }
                        refreshed.Add(new NativeCatalogLocatorObservation(priorObservation.Configured, resolution));
                    }
                    catch
                    {
                        resolution.Dispose();
                        throw;
                    }
                }

                replacements = new Dictionary<PhysicalRootIdentity, NativeCatalogLockedRoot>();
                var nextDescriptors = _groupDescriptors
                    .Where(descriptor =>
                        (adoptedDescriptor is null || descriptor.SharedStateSlot != adoptedDescriptor.SharedStateSlot) &&
                        !descriptor.Participants.Any(participant => outcomes.ContainsKey(participant.RootIdentity)))
                    .Concat(adoptedDescriptor is null ? [] : [adoptedDescriptor])
                    .DistinctBy(static descriptor => descriptor.IntentDigest)
                    .Select(static descriptor => descriptor.Copy())
                    .ToArray();
                foreach (var group in refreshed.Where(static item => item.Resolution.MembershipCandidate is not null)
                             .GroupBy(static item => item.Resolution.RootIdentity!, EqualityComparer<PhysicalRootIdentity>.Default))
                {
                    var aliases = group.ToArray();
                    var first = aliases[0].Resolution;
                    if (aliases.Any(alias => alias.Resolution.MembershipCandidate!.LedgerDigest != first.MembershipCandidate!.LedgerDigest ||
                            alias.Resolution.MembershipLedgerIdentity != first.MembershipLedgerIdentity))
                        throw Refused("Configured aliases produced different post-transaction membership evidence.", first.RootIdentity!);
                    var old = RequireRoot(first.RootIdentity!, _registry._catalogOwnerMint);
                    var candidate = first.MembershipCandidate!;
                    var policy = RequireCatalogLedgerShape(candidate, old.RootIdentity, old.EnrollmentEpoch, nextDescriptors);
                    var slots = candidate.Members.Select(GetSlot).ToArray();
                    if (!slots.SequenceEqual(old.LockedSlots!))
                        throw Refused("An adopted transaction changed the catalog's held root-local member-lock obligations.", old.RootIdentity);
                    var scope = new MemberLocatorReplayScope(candidate,
                        policy == LocatorReplayBindingPolicy.PendingStateRecovery);
                    IReadOnlyDictionary<string, ResolvedMemberStateLocation>? locations = null;
                    try
                    {
                        locations = _registry.ResolveMemberLocatorMap(candidate, scope, policy);
                        replacements.Add(old.RootIdentity, new NativeCatalogLockedRoot(aliases, old.RootIdentity,
                            old.EnrollmentEpoch, candidate.LedgerDigest, first.MembershipLedgerIdentity!)
                        {
                            LockedLedger = candidate,
                            LockedLedgerIdentity = first.MembershipLedgerIdentity,
                            InitialLedger = candidate,
                            InitialLedgerIdentity = first.MembershipLedgerIdentity,
                            LockedSlots = old.LockedSlots,
                            ReplayPolicy = policy,
                            Scope = scope,
                            Locations = locations
                        });
                    }
                    catch
                    {
                        if (locations is not null) DisposeLocations(locations.Values);
                        scope.Expire();
                        throw;
                    }
                }

                RequireCatalogGroupDescriptors(replacements.Values.ToArray(), nextDescriptors);

                // Swap only after every locator and replacement member map has passed exact native replay.
                foreach (var old in _roots.Values)
                {
                    old.Scope?.Expire();
                    if (old.Locations is not null) DisposeLocations(old.Locations.Values);
                }
                foreach (var old in _observations)
                    old.Resolution.Dispose();
                _observations.Clear();
                _observations.AddRange(refreshed);
                _roots = replacements!;
                _groupDescriptors.Clear();
                _groupDescriptors.AddRange(nextDescriptors);
                replacements = null;
            }
            catch
            {
                foreach (var replacement in replacements?.Values ?? Enumerable.Empty<NativeCatalogLockedRoot>())
                {
                    replacement.Scope?.Expire();
                    if (replacement.Locations is not null) DisposeLocations(replacement.Locations.Values);
                }
                foreach (var observation in refreshed)
                    if (!_observations.Contains(observation)) observation.Resolution.Dispose();
                throw;
            }
        }

        internal void Poison()
        {
            lock (_gate) _poisoned = true;
        }

        internal void RequireBorrowMint(object mint)
        {
            if (!ReferenceEquals(mint, _registry._catalogOwnerMint))
                throw Refused("Only the minting registry can create a native catalog borrow.");
        }

        internal void RequireDescriptorEvidence(GroupPublicationDescriptorV2 descriptor, object mint)
        {
            if (!ReferenceEquals(mint, _registry._catalogOwnerMint))
                throw Refused("Only the minting registry can validate adopted catalog descriptor evidence.");
            RequireCatalogGroupDescriptors(_roots.Values.ToArray(), [descriptor]);
        }

        private void ReleaseBorrowRequest()
        {
            lock (_gate) ReleaseBorrowRequestLocked();
        }

        private void ReleaseBorrowRequestLocked()
        {
            _borrowRequests--;
            if (_closing && _borrowRequests == 0)
                _drained?.TrySetResult();
        }

        internal void ReturnBorrow(object mint)
        {
            if (!ReferenceEquals(mint, _registry._catalogOwnerMint))
                throw Refused("Only a registry-minted borrow can return native catalog ownership.");
            _operationGate.Release();
            ReleaseBorrowRequest();
        }

        public ValueTask DisposeAsync()
        {
            TaskCompletionSource? start = null;
            Task disposeTask;
            lock (_gate)
            {
                if (_disposeTask is null)
                {
                    _closing = true;
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _disposeTask = completion.Task;
                    start = completion;
                }
                disposeTask = _disposeTask;
            }
            if (start is not null)
                _ = CompleteDisposeAsync(start);
            return new ValueTask(disposeTask);
        }

        private async Task CompleteDisposeAsync(TaskCompletionSource completion)
        {
            try
            {
                await DisposeCoreAsync().ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private async Task DisposeCoreAsync()
        {
            Task waitForBorrows;
            lock (_gate)
            {
                waitForBorrows = _borrowRequests == 0
                    ? Task.CompletedTask
                    : (_drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            await waitForBorrows.ConfigureAwait(false);

            var errors = new List<Exception>();
            foreach (var root in _roots.Values.Reverse())
            {
                root.Scope?.Expire();
                if (root.Locations is not null)
                {
                    try { DisposeLocations(root.Locations.Values); } catch (Exception exception) { errors.Add(exception); }
                }
            }
            if (_union is not null)
            {
                try { await _union.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { errors.Add(exception); }
                _union = null;
            }
            foreach (var observation in _observations.AsEnumerable().Reverse())
            {
                try { observation.Resolution.Dispose(); } catch (Exception exception) { errors.Add(exception); }
            }
            if (errors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
            if (errors.Count > 1) throw new AggregateException("Native catalog owner cleanup failed.", errors);
        }
    }

    /// <summary>A single serialized use of a registry-minted full-catalog owner.</summary>
    internal sealed class NativeCatalogOwnerBorrow : IAsyncDisposable
    {
        private readonly NativeCatalogOwner _owner;
        private readonly RootMembershipRegistry _registry;
        private readonly object _gate = new();
        private int _disposed;
        private bool _operationActive;

        internal NativeCatalogOwnerBorrow(NativeCatalogOwner owner, RootMembershipRegistry registry, object mint)
        {
            owner.RequireBorrowMint(mint);
            _owner = owner;
            _registry = registry;
        }

        internal NativeCatalogOwner Owner => _owner;

        internal void RequireRegistry(RootMembershipRegistry registry)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                if (!ReferenceEquals(registry, _registry))
                    throw Refused("A catalog owner minted by another membership registry cannot authorize this operation.");
            }
        }

        internal Operation EnterOperation(RootMembershipRegistry registry)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                if (!ReferenceEquals(registry, _registry))
                    throw Refused("A catalog owner minted by another membership registry cannot authorize this operation.");
                if (_operationActive)
                    throw Refused("One catalog-owner borrow cannot run overlapping adopted mutations.");
                _owner.RequireOperationMayStart();
                _operationActive = true;
                return new Operation(this);
            }
        }

        private void ExitOperation()
        {
            var releaseBorrow = false;
            lock (_gate)
            {
                if (!_operationActive)
                    return;
                _operationActive = false;
                releaseBorrow = _disposed != 0;
            }
            if (releaseBorrow)
            _owner.ReturnBorrow(_registry._catalogOwnerMint);
        }

        public ValueTask DisposeAsync()
        {
            var releaseBorrow = false;
            lock (_gate)
            {
                if (_disposed != 0)
                    return ValueTask.CompletedTask;
                _disposed = 1;
                releaseBorrow = !_operationActive;
            }
            if (releaseBorrow)
                _owner.ReturnBorrow(_registry._catalogOwnerMint);
            return ValueTask.CompletedTask;
        }

        internal sealed class Operation : IDisposable
        {
            private NativeCatalogOwnerBorrow? _borrow;
            internal Operation(NativeCatalogOwnerBorrow borrow) => _borrow = borrow;
            public void Dispose() => Interlocked.Exchange(ref _borrow, null)?.ExitOperation();
        }
    }

    internal async Task<RootMembershipRecord> RecoverLocalAsync(
        NativeCatalogOwnerBorrow owner,
        PhysicalRootIdentity rootIdentity,
        CancellationToken cancellationToken,
        Action<RootMembershipPublicationPoint>? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return await owner.Owner.RecoverLocalAsync(owner, rootIdentity, cancellationToken, checkpoint, _catalogOwnerMint)
            .ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<RootMembershipRecord>> PublishNativeGroupAsync(
        NativeCatalogOwnerBorrow owner,
        GroupPublicationDescriptorV2 descriptor,
        StoreStateRecord nextState,
        CancellationToken cancellationToken,
        Action<NativeGroupPublicationPoint, PhysicalRootIdentity?>? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(nextState);
        owner.RequireRegistry(this);
        using var operation = owner.EnterOperation(this);
        owner.Owner.RevalidateRetainedEvidence();
        await using var group = await AcquireNativeGroupOwnerAsync(owner, descriptor, cancellationToken, checkpoint).ConfigureAwait(false);
        try
        {
            var result = await PublishNativeGroupCoreAsync(group, descriptor, nextState, cancellationToken, checkpoint)
                .ConfigureAwait(false);
            await owner.Owner.RefreshAfterOwnedOutcomesAsync(group.Participants.ToDictionary(
                static participant => participant.Request.RootIdentity,
                participant => (group.ReadCurrent(participant), participant.Transaction!.LedgerIdentity)),
                descriptor, _catalogOwnerMint)
                .ConfigureAwait(false);
            group.RequireBoundMarker();
            return result;
        }
        catch
        {
            owner.Owner.Poison();
            throw;
        }
    }

    internal async Task<IReadOnlyList<RootMembershipRecord>> RecoverNativeGroupAsync(
        NativeCatalogOwnerBorrow owner,
        GroupPublicationDescriptorV2 descriptor,
        CancellationToken cancellationToken,
        Action<NativeGroupPublicationPoint, PhysicalRootIdentity?>? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(descriptor);
        owner.RequireRegistry(this);
        using var operation = owner.EnterOperation(this);
        owner.Owner.RevalidateRetainedEvidence();
        await using var group = await AcquireNativeGroupOwnerAsync(owner, descriptor, cancellationToken, checkpoint).ConfigureAwait(false);
        try
        {
            var result = await RecoverNativeGroupCoreAsync(group, descriptor, cancellationToken, checkpoint)
                .ConfigureAwait(false);
            await owner.Owner.RefreshAfterOwnedOutcomesAsync(group.Participants.ToDictionary(
                static participant => participant.Request.RootIdentity,
                participant => (group.ReadCurrent(participant), participant.Transaction!.LedgerIdentity)),
                descriptor, _catalogOwnerMint)
                .ConfigureAwait(false);
            group.RequireBoundMarker();
            return result;
        }
        catch
        {
            owner.Owner.Poison();
            throw;
        }
    }

    private async Task<NativeGroupPublicationOwner> AcquireNativeGroupOwnerAsync(
        NativeCatalogOwnerBorrow owner,
        GroupPublicationDescriptorV2 descriptor,
        CancellationToken cancellationToken,
        Action<NativeGroupPublicationPoint, PhysicalRootIdentity?>? checkpoint)
    {
        owner.RequireRegistry(this);
        cancellationToken.ThrowIfCancellationRequested();
        if (descriptor.Participants.Count == 0)
            throw Refused("A native group adoption requires at least one immutable participant.");
        owner.Owner.RequireDescriptorEvidence(descriptor, _catalogOwnerMint);
        var participants = new List<NativeGroupParticipant>(descriptor.Participants.Count);
        try
        {
            foreach (var local in descriptor.Participants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = owner.Owner.RequireRoot(local.RootIdentity, _catalogOwnerMint);
                if (root.EnrollmentEpoch != local.EnrollmentEpoch)
                    throw Refused("A group participant does not match the retained catalog enrollment epoch.", local.RootIdentity);
                RequireGroupLedgerForDescriptor(root.Ledger, local, descriptor);
                var request = new NativeGroupRootRequest(root.PrimaryResolution.AuthorityRoot!, local.RootIdentity,
                    local.EnrollmentEpoch);
                var control = OpenControl(request.RootHandle);
                IAsyncDisposable? share = null;
                MemberLocatorReplayScope? scope = null;
                IReadOnlyDictionary<string, ResolvedMemberStateLocation>? locations = null;
                Transaction? transaction = null;
                try
                {
                    var current = ReadLedger(request.RootHandle, control, out var identity, root.LedgerIdentity);
                    RequireGroupLedgerForDescriptor(current, local, descriptor);
                    RequireSameDigest(root.Ledger, current);
                    scope = new MemberLocatorReplayScope(current);
                    locations = ResolveMemberLocatorMap(current, scope, LocatorReplayBindingPolicy.GroupAcknowledged);
                    var selected = locations[local.PriorMember.MemberId];
                    if (selected.Slot != descriptor.SharedStateSlot)
                        throw Refused("The adopted group participant does not resolve to the immutable shared state slot.", local.RootIdentity);
                    selected.Revalidate();
                    share = owner.Owner.CreateShare();
                    transaction = new Transaction(this, request.RootHandle, control, share, current, identity);
                    share = null;
                    var participantControl = control;
                    control = null!;
                    participants.Add(new NativeGroupParticipant(request, local, null!,
                        participantControl, current, identity)
                    {
                        LockedLedger = current,
                        LockedLedgerIdentity = identity,
                        Scope = scope,
                        Locations = locations,
                        SelectedLocation = selected,
                        Transaction = transaction
                    });
                    scope = null;
                    locations = null;
                    transaction = null;
                }
                finally
                {
                    if (locations is not null) DisposeLocations(locations.Values);
                    scope?.Expire();
                    if (transaction is not null) await transaction.DisposeAsync().ConfigureAwait(false);
                    else
                    {
                        if (share is not null) await share.DisposeAsync().ConfigureAwait(false);
                        control?.Dispose();
                    }
                }
            }
            return await BindNativeGroupPublicationOwnerAsync(descriptor, participants, cancellationToken, checkpoint)
                .ConfigureAwait(false);
        }
        catch
        {
            for (var index = participants.Count - 1; index >= 0; index--)
            {
                var participant = participants[index];
                if (participant.Locations is not null) DisposeLocations(participant.Locations.Values);
                participant.Scope?.Expire();
                await participant.Transaction!.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }

    }
}

internal sealed class NativeCatalogLocatorObservation(TrustedPackageStoreRoot configured, ResolvedPackageStorePath resolution)
{
    internal TrustedPackageStoreRoot Configured { get; } = configured;
    internal ResolvedPackageStorePath Resolution { get; set; } = resolution;
}

internal sealed class NativeCatalogLockedRoot(
    IReadOnlyList<NativeCatalogLocatorObservation> aliases,
    PhysicalRootIdentity rootIdentity,
    long enrollmentEpoch,
    string ledgerDigest,
    PhysicalFileIdentity ledgerIdentity)
{
    internal IReadOnlyList<NativeCatalogLocatorObservation> Aliases { get; set; } = aliases;
    internal PhysicalRootIdentity RootIdentity { get; } = rootIdentity;
    internal long EnrollmentEpoch { get; } = enrollmentEpoch;
    internal string LedgerDigest { get; } = ledgerDigest;
    internal PhysicalFileIdentity ResolvedLedgerIdentity { get; } = ledgerIdentity;
    internal ResolvedPackageStorePath PrimaryResolution => Aliases[0].Resolution;
    internal PhysicalStoreLock.RootLockScope? RootScope { get; set; }
    internal PhysicalStoreDirectoryHandle? ControlDirectory { get; set; }
    internal RootMembershipRecord? InitialLedger { get; set; }
    internal PhysicalFileIdentity? InitialLedgerIdentity { get; set; }
    internal RootMembershipRecord? LockedLedger { get; set; }
    internal PhysicalFileIdentity? LockedLedgerIdentity { get; set; }
    internal RootMembershipRecord Ledger => LockedLedger ?? throw new InvalidOperationException("The catalog root has not been locked.");
    internal PhysicalFileIdentity LedgerIdentity => LockedLedgerIdentity ?? throw new InvalidOperationException("The catalog root has not been locked.");
    internal StateSlotIdentity[]? LockedSlots { get; set; }
    internal RootMembershipRegistry.LocatorReplayBindingPolicy ReplayPolicy { get; set; }
    internal RootMembershipRegistry.MemberLocatorReplayScope? Scope { get; set; }
    internal IReadOnlyDictionary<string, ResolvedMemberStateLocation>? Locations { get; set; }
}
