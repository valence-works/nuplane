using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds.Configuration;
using Nuplane.Reconciliation;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;

namespace Nuplane.Store.Maintenance;

/// <summary>Performs explicit quiescent enrollment and recovery using only frozen host declarations.</summary>
internal sealed class PackageStoreEnrollmentService
{
    private readonly IPhysicalStoreFileSystem _files;
    private readonly RootMembershipRegistry _registry;
    private readonly IStoreStateSerializer _serializer;
    private readonly ITrustedPackageStoreRootCatalog _rootCatalog;
    private readonly IPackageStoreRootResolver _rootResolver;
    private readonly ITrustedPackageStoreMemberDeclarationCatalog _declarations;
    private readonly IReadOnlyList<Nuplane.Abstractions.FeedDefinition> _configuredFeeds;

    internal PackageStoreEnrollmentService(
        IPhysicalStoreFileSystem files,
        RootMembershipRegistry registry,
        IStoreStateSerializer serializer,
        ITrustedPackageStoreRootCatalog rootCatalog,
        IPackageStoreRootResolver rootResolver,
        ITrustedPackageStoreMemberDeclarationCatalog declarations,
        FeedResolutionOptions feedOptions)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(rootCatalog);
        ArgumentNullException.ThrowIfNull(rootResolver);
        ArgumentNullException.ThrowIfNull(declarations);
        ArgumentNullException.ThrowIfNull(feedOptions);
        if (!registry.UsesFileSystem(files))
            throw new ArgumentException("Enrollment and membership registry must share one native filesystem provider.", nameof(registry));
        _files = files;
        _registry = registry;
        _serializer = serializer;
        _rootCatalog = rootCatalog;
        _rootResolver = rootResolver;
        _declarations = declarations;
        _configuredFeeds = Array.AsReadOnly(feedOptions.Feeds.ToArray());
    }

    internal Task<PackageStoreEnrollmentOperationResult> EnrollAsync(
        PackageStoreEnrollmentOperationRequest request,
        CancellationToken cancellationToken = default)
        => EnrollOrRecoverAsync(request, recoverExisting: false, cancellationToken);

    internal Task<PackageStoreEnrollmentOperationResult> RecoverEnrollmentAsync(
        PackageStoreEnrollmentOperationRequest request,
        CancellationToken cancellationToken = default)
        => EnrollOrRecoverAsync(request, recoverExisting: true, cancellationToken);

    internal async Task<PackageStoreEnrollmentOperationResult> MigrateAsync(
        PackageStoreEnrollmentOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.QuiescentCutoverConfirmed)
            throw Refused("Membership migration requires explicit confirmation that every store user remains quiescent.");
        if (!_declarations.IsConfigured || !_declarations.IsComplete)
            throw Refused("Membership migration requires a frozen, explicitly complete trusted member declaration set.");
        if (!_rootResolver.TryResolve(request.RootLabel, out var configuredRoot) || configuredRoot is null ||
            !_rootCatalog.Roots.Any(root =>
                string.Equals(root.Label, configuredRoot.Label, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(root.RootPath, configuredRoot.RootPath, StringComparison.Ordinal)))
            throw Refused("The requested migration root does not match the frozen trusted root catalog.");

        var declarations = _declarations.ForRoot(request.RootLabel).ToArray();
        ValidateUniqueDeclarations(declarations);
        var declarationIds = declarations.Select(static declaration => declaration.MemberId).ToHashSet(StringComparer.Ordinal);
        if (declarations.Length == 0 || !declarationIds.SetEquals(request.IntendedMemberIds))
            throw Refused("Migration target IDs must exactly match the frozen trusted member declaration set.");
        if (request.RetireLegacyRecoveryMemberIds.Any(memberId => !declarationIds.Contains(memberId)) ||
            request.RecoveryEvidence.Any(evidence => !declarationIds.Contains(evidence.MemberId)))
            throw Refused("Legacy recovery evidence and retirement intent may name only intended target members.");
        if (request.RetireLegacyRecoveryMemberIds.Any(memberId =>
                !request.RecoveryEvidence.Any(evidence => string.Equals(evidence.MemberId, memberId, StringComparison.Ordinal))))
            throw Refused("Legacy recovery retirement requires source-bound graph evidence for every affected member.");

        var authority = new PackageStoreAuthorityResolver(_files, _registry);
        using var rootResolution = authority.ResolveTrustedCatalogRoot(configuredRoot.RootPath);
        rootResolution.Revalidate();
        if (rootResolution.Target is not PhysicalStoreDirectoryHandle root || rootResolution.IsProspectiveConfiguredRoot)
            throw Refused("Membership migration requires an existing configured physical package-store root.");
        var rootInfo = _files.InspectHandle(root);
        if (rootInfo.Kind != PhysicalStoreEntryKind.Directory)
            throw Refused("Membership migration requires an existing configured physical package-store root.");
        var rootIdentity = rootResolution.RootIdentity ?? new PhysicalRootIdentity(rootInfo.Identity);
        if (rootIdentity != request.ExpectedPhysicalRootIdentity)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.RootMismatch,
                "The configured root no longer has the physical identity confirmed in the migration request.", rootIdentity);

        var declaredTargets = declarations.Select(declaration => new RootMemberRecord(declaration.MemberId,
            declaration.StateFileLocator, new RootMemberRecord.DeclaredBinding())).ToArray();
        return await _registry.WithQuiescentMembershipMigrationLocationsAsync(root, rootIdentity,
            request.ExpectedEnrollmentEpoch, request.ExpectedCurrentMemberIds, declaredTargets,
            request.RetireMemberIds, authority, request.QuiescentCutoverConfirmed, recoverExisting: false,
            (locked, token) => MigrateAndCompleteAsync(locked, request, recoveringMigration: false, cancellationToken: token),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PackageStoreEnrollmentOperationResult> EnrollOrRecoverAsync(
        PackageStoreEnrollmentOperationRequest request,
        bool recoverExisting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.QuiescentCutoverConfirmed)
            throw Refused("Enrollment and recovery require explicit confirmation that every store user remains quiescent.");
        if (!_declarations.IsConfigured || !_declarations.IsComplete)
            throw Refused("Enrollment requires a frozen, explicitly complete trusted member declaration set.");
        if (!_rootResolver.TryResolve(request.RootLabel, out var configuredRoot) || configuredRoot is null)
            throw Refused("The requested package-store root label is not configured.");
        if (!_rootCatalog.Roots.Any(root =>
                string.Equals(root.Label, configuredRoot.Label, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(root.RootPath, configuredRoot.RootPath, StringComparison.Ordinal)))
            throw Refused("The resolved maintenance root no longer matches the frozen trusted root catalog.");

        var declarations = _declarations.ForRoot(request.RootLabel).ToArray();
        if (declarations.Length == 0 || declarations.Any(declaration =>
                !string.Equals(declaration.RootLabel, request.RootLabel, StringComparison.OrdinalIgnoreCase)))
            throw Refused("The selected root has no matching complete trusted member declarations.");
        ValidateUniqueDeclarations(declarations);
        var declarationIds = declarations.Select(static declaration => declaration.MemberId).ToHashSet(StringComparer.Ordinal);
        var intendedIds = request.IntendedMemberIds.ToHashSet(StringComparer.Ordinal);
        if (intendedIds.Count == 0 || !declarationIds.SetEquals(intendedIds))
            throw Refused("Initial enrollment currently requires the intended member IDs to exactly match the frozen declaration roster.");
        if (request.RetireLegacyRecoveryMemberIds.Any(memberId => !intendedIds.Contains(memberId)))
            throw Refused("Legacy recovery retirement must name only intended members.");
        if (request.RecoveryEvidence.Any(evidence => !intendedIds.Contains(evidence.MemberId)))
            throw Refused("Recovery evidence may only be supplied for an intended member.");
        if (request.RetireLegacyRecoveryMemberIds.Any(memberId =>
                !request.RecoveryEvidence.Any(evidence => string.Equals(evidence.MemberId, memberId, StringComparison.Ordinal))))
            throw Refused("Legacy recovery retirement requires source-bound graph evidence for every affected member.");

        var authority = new PackageStoreAuthorityResolver(_files, _registry);
        using var rootResolution = authority.ResolveTrustedCatalogRoot(configuredRoot.RootPath);
        rootResolution.Revalidate();
        if (rootResolution.Target is not PhysicalStoreDirectoryHandle root || rootResolution.IsProspectiveConfiguredRoot)
            throw Refused("Enrollment requires an existing configured physical package-store root.");
        var rootInfo = _files.InspectHandle(root);
        if (rootInfo.Kind != PhysicalStoreEntryKind.Directory)
            throw Refused("Enrollment requires an existing configured physical package-store root.");
        var rootIdentity = rootResolution.RootIdentity ?? new PhysicalRootIdentity(rootInfo.Identity);
        if (rootResolution.MembershipCandidate is { } observedCandidate && observedCandidate.RootIdentity != rootIdentity)
            throw Refused("The observed membership candidate does not bind the configured physical root.", rootIdentity);
        if (rootIdentity != request.ExpectedPhysicalRootIdentity)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.RootMismatch,
                "The configured root no longer has the physical identity confirmed in the request.", rootIdentity);

        var candidate = rootResolution.MembershipCandidate;
        if (candidate is null)
        {
            if (recoverExisting || request.ExpectedEnrollmentEpoch != 1 || request.ExpectedCurrentMemberIds.Count != 0)
                throw Refused("Initial enrollment requires an absent ledger, epoch 1 and an empty expected current roster.", rootIdentity);
            if (request.RetireMemberIds.Count != 0)
                throw Refused("RetireMemberIds applies only to a verified prior Complete roster.", rootIdentity);
            var declared = declarations.Select(declaration => new RootMemberRecord(declaration.MemberId,
                declaration.StateFileLocator, new RootMemberRecord.DeclaredBinding())).ToArray();
            var initialized = _registry.InitializeIncomplete(root, rootIdentity, 1, declared,
                request.QuiescentCutoverConfirmed, cancellationToken);
            using var refreshedResolution = authority.ResolveTrustedCatalogRoot(configuredRoot.RootPath);
            refreshedResolution.Revalidate();
            var refreshedRoot = refreshedResolution.Target is PhysicalStoreDirectoryHandle refreshedHandle
                ? new PhysicalRootIdentity(_files.InspectHandle(refreshedHandle).Identity)
                : throw Refused("The configured package-store root changed kind after initial declaration publication.", rootIdentity);
            if (refreshedRoot != rootIdentity || refreshedResolution.MembershipCandidate is not { } refreshedCandidate ||
                !string.Equals(refreshedCandidate.LedgerDigest, initialized.LedgerDigest, StringComparison.Ordinal) ||
                refreshedCandidate.Status != RootMembershipStatus.Incomplete)
                throw Refused("The published initial declaration could not be replayed from the configured root path.", rootIdentity);
            return await BindPublishAndCompleteAsync(root, rootIdentity, request, declarations, cancellationToken)
                .ConfigureAwait(false);
        }

        var actualMemberIds = candidate.Members.Select(static member => member.MemberId).ToHashSet(StringComparer.Ordinal);
        if (candidate.PendingMembershipMigration is { } pendingMigration)
        {
            if (!recoverExisting)
                throw Refused("A pending roster migration requires explicit recovery.", rootIdentity);
            if (candidate.EnrollmentEpoch != request.ExpectedEnrollmentEpoch ||
                !actualMemberIds.SetEquals(request.ExpectedCurrentMemberIds))
                throw Refused("Migration recovery must match the exact observed root, Incomplete epoch and old/new roster.", rootIdentity);
            var migrationTargets = declarations.Select(declaration => new RootMemberRecord(declaration.MemberId,
                declaration.StateFileLocator, new RootMemberRecord.DeclaredBinding())).ToArray();
            return await _registry.WithQuiescentMembershipMigrationLocationsAsync(root, rootIdentity,
                pendingMigration.PriorCompleteLedger.EnrollmentEpoch, request.ExpectedCurrentMemberIds, migrationTargets,
                request.RetireMemberIds, authority, request.QuiescentCutoverConfirmed, recoverExisting: true,
                (locked, token) => MigrateAndCompleteAsync(locked, request, recoveringMigration: true, cancellationToken: token),
                cancellationToken).ConfigureAwait(false);
        }
        if (!actualMemberIds.SetEquals(request.ExpectedCurrentMemberIds))
            throw Refused("The current membership roster does not exactly match ExpectedCurrentMemberIds.", rootIdentity);
        if (request.RetireMemberIds.Count != 0)
            throw Refused("RetireMemberIds applies only to a verified prior Complete roster migration.", rootIdentity);
        if (candidate.RootIdentity != rootIdentity || candidate.EnrollmentEpoch != request.ExpectedEnrollmentEpoch)
            throw Refused("The current membership root or epoch differs from the explicit operation precondition.", rootIdentity);

        if (candidate.Status == RootMembershipStatus.Complete)
        {
            if (recoverExisting)
                throw Refused("RecoverEnrollmentAsync only resumes an Incomplete enrollment; Complete membership requires explicit roster migration.", rootIdentity);
            throw Refused("The root is already Complete; use the membership migration operation for a roster change.", rootIdentity);
        }
        if (!recoverExisting)
            throw Refused("The root is Incomplete; explicit RecoverEnrollmentAsync is required to resume it.", rootIdentity);
        if (candidate.PendingGroupPublicationV2 is not null)
            throw Refused("A pending multi-root state publication requires its dedicated group recovery path.", rootIdentity);
        if (!candidate.TargetMemberIds.ToHashSet(StringComparer.Ordinal).SetEquals(intendedIds))
            throw Refused("Recovery intent must exactly match the persisted incomplete target roster.", rootIdentity);
        if (candidate.RetiredMembers.Count != 0 || request.RetireMemberIds.Count != 0)
            throw Refused("This recovery path cannot change or add roster-retirement intent.", rootIdentity);

        if (candidate.PendingStateCommit is not null)
        {
            var scope = new RootMembershipRegistry.MemberLocatorReplayScope(candidate, allowPendingStateCommit: true);
            var parents = new Dictionary<string, PhysicalStoreDirectoryHandle>(StringComparer.Ordinal);
            var resolved = new List<ResolvedMemberStateLocation>();
            try
            {
                foreach (var member in candidate.Members)
                {
                    var location = authority.ResolveMemberStateLocation(member.ConfiguredLocator, scope);
                    resolved.Add(location);
                    parents.Add(member.MemberId, location.Parent);
                }
                cancellationToken.ThrowIfCancellationRequested();
                candidate = await _registry.RecoverAsync(root, parents, cancellationToken).ConfigureAwait(false);
                var recovered = _registry.ReadCandidate(root, out var recoveredLedgerIdentity);
                if (!string.Equals(candidate.LedgerDigest, recovered.LedgerDigest, StringComparison.Ordinal))
                    throw Refused("The recovered membership changed before the locked result could be replayed.", rootIdentity);
                rootResolution.RevalidateOwnedLedgerOutcome(recovered, recoveredLedgerIdentity);
                candidate = recovered;
            }
            finally
            {
                foreach (var location in resolved.AsEnumerable().Reverse())
                    location.Dispose();
                scope.Expire();
            }
        }

        if (candidate.Members.Any(static member => member.Binding is RootMemberRecord.DeclaredBinding))
            return await BindPublishAndCompleteAsync(root, rootIdentity, request, declarations, cancellationToken)
                .ConfigureAwait(false);

        return await _registry.WithQuiescentBoundIncompleteMemberLocationsAsync(root, rootIdentity,
            request.ExpectedEnrollmentEpoch, request.QuiescentCutoverConfirmed,
            (locked, token) => PublishAndCompleteAsync(locked, request, token), cancellationToken,
            enrollmentOwner: true).ConfigureAwait(false);
    }

    private async Task<PackageStoreEnrollmentOperationResult> BindPublishAndCompleteAsync(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity rootIdentity,
        PackageStoreEnrollmentOperationRequest request,
        IReadOnlyList<TrustedPackageStoreMemberDeclaration> declarations,
        CancellationToken cancellationToken)
    {
        var declarationMembers = declarations.Select(declaration => new RootMemberRecord(declaration.MemberId,
            declaration.StateFileLocator, new RootMemberRecord.DeclaredBinding())).ToArray();
        var initial = _registry.ReadCandidate(root);
        if (initial.RootIdentity != rootIdentity || initial.EnrollmentEpoch != request.ExpectedEnrollmentEpoch ||
            initial.Status != RootMembershipStatus.Incomplete || initial.PendingStateCommit is not null)
            throw Refused("The initial Incomplete declaration changed before all member locks were acquired.", rootIdentity);

        var scope = new RootMembershipRegistry.MemberLocatorReplayScope(initial);
        var resolved = new Dictionary<string, ResolvedMemberStateLocation>(StringComparer.Ordinal);
        try
        {
            foreach (var declaration in declarations)
                resolved.Add(declaration.MemberId, new PackageStoreAuthorityResolver(_files, _registry)
                    .ResolveMemberStateLocation(declaration.StateFileLocator, scope));
            var heldParents = resolved.ToDictionary(static pair => pair.Key,
                static pair => (pair.Value.Parent, pair.Value.RequestedBasename), StringComparer.Ordinal);
            return await _registry.BindDeclaredMembersAndRunAsync(root, rootIdentity,
                request.ExpectedEnrollmentEpoch, declarationMembers, heldParents,
                request.QuiescentCutoverConfirmed,
                (locked, token) => PublishAndCompleteAsync(locked, request, token),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var location in resolved.Values.Reverse())
                location.Dispose();
            scope.Expire();
        }
    }

    private async Task<PackageStoreEnrollmentOperationResult> PublishAndCompleteAsync(
        RootMembershipRegistry.LockedMemberLocations locked,
        PackageStoreEnrollmentOperationRequest request,
        CancellationToken cancellationToken)
    {
        locked.RequirePayloadSerializer(_serializer);
        var ledger = locked.Ledger;
        if (ledger.RootIdentity != request.ExpectedPhysicalRootIdentity ||
            ledger.EnrollmentEpoch != request.ExpectedEnrollmentEpoch ||
            !ledger.Members.Select(static member => member.MemberId).ToHashSet(StringComparer.Ordinal)
                .SetEquals(request.ExpectedCurrentMemberIds.Count == 0
                    ? request.IntendedMemberIds
                    : request.ExpectedCurrentMemberIds))
            throw Refused("The locked enrollment ledger no longer matches the explicit root/epoch/member preconditions.", ledger.RootIdentity);
        if (!ledger.TargetMemberIds.ToHashSet(StringComparer.Ordinal).SetEquals(request.IntendedMemberIds))
            throw Refused("The locked target roster differs from the requested intended member set.", ledger.RootIdentity);

        var results = new List<PackageStoreEnrollmentMemberResult>(request.IntendedMemberIds.Count);
        foreach (var memberId in request.IntendedMemberIds.OrderBy(static id => id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ledger = locked.Ledger;
            var member = ledger.Members.Single(item => string.Equals(item.MemberId, memberId, StringComparison.Ordinal));
            var state = await locked.ReadMemberStateAsync(memberId, cancellationToken).ConfigureAwait(false);
            StoreStateRecord candidate;
            var diagnostics = new List<string>();
            var retireLegacyRecovery = request.RetireLegacyRecoveryMemberIds.Contains(memberId, StringComparer.Ordinal);
            var hasEvidence = TryGetEvidence(request, memberId, out var suppliedEvidence);
            if (member.Binding is RootMemberRecord.AcknowledgedBinding acknowledged)
            {
                if (retireLegacyRecovery || hasEvidence)
                    throw Refused($"Member '{memberId}' is already protected; source-bound legacy recovery retirement applies only to an unprotected source state.", ledger.RootIdentity);
                if (state?.ProtectionRecord is not { } sourceProtection ||
                    !sourceProtection.HasSamePayloadAs(acknowledged.ProtectionRecord))
                    throw Refused($"Member '{memberId}' no longer matches its acknowledged source protection.", ledger.RootIdentity);
                if (sourceProtection.EnrollmentEpoch == ledger.EnrollmentEpoch)
                {
                    results.Add(new(memberId, PackageStoreEnrollmentMemberDisposition.Recovered, []));
                    continue;
                }
                candidate = PackageStoreProtectionRecordFactory.WithProtection(state, ledger.RootIdentity,
                    ledger.EnrollmentEpoch, memberId, checked(sourceProtection.Revision + 1),
                    sourceProtection.ActiveClosure, sourceProtection.RecoverableClosure,
                    sourceProtection.RetiredGraphs, sourceProtection.LegacyUnknownRecovery);
                diagnostics.Add("Rebound verified protection to the current enrollment epoch.");
            }
            else if (state is null)
            {
                if (retireLegacyRecovery || hasEvidence)
                    throw Refused($"Member '{memberId}' has no source state to bind supplied recovery evidence.", ledger.RootIdentity);
                var knownEmpty = KnownEmptyClosure();
                candidate = PackageStoreProtectionRecordFactory.WithProtection(StoreStateRecord.Empty(),
                    ledger.RootIdentity, ledger.EnrollmentEpoch, memberId, 1, knownEmpty, knownEmpty);
            }
            else if (state.ProtectionRecord is not null || state.ProtectionBundle is not null)
            {
                throw Refused($"Member '{memberId}' has protection metadata that is not bound by this Incomplete ledger.", ledger.RootIdentity);
            }
            else if (hasEvidence)
            {
                candidate = BuildFromEvidence(ledger, memberId, state, locked.Locations[memberId], suppliedEvidence,
                    retireLegacyRecovery);
                diagnostics.Add(retireLegacyRecovery
                    ? "Applied source-bound active graph evidence and recorded the explicitly retired legacy LKG entries."
                    : "Applied source-bound active/recoverable graph evidence after semantic and native verification.");
            }
            else if (IsKnownEmptyLegacy(state))
            {
                var knownEmpty = KnownEmptyClosure();
                candidate = PackageStoreProtectionRecordFactory.WithProtection(state, ledger.RootIdentity,
                    ledger.EnrollmentEpoch, memberId, 1, knownEmpty, knownEmpty);
            }
            else
            {
                if (HasLegacyRecoveryBeyondActive(state))
                    throw Refused($"Member '{memberId}' contains legacy LKG entries outside its active state; supply source-bound graph evidence and explicitly retire those entries, or keep enrollment Incomplete.", ledger.RootIdentity);
                using var reconstructed = await LegacyActiveGraphCandidateProducer.CreateAsync(_files, _registry,
                    _serializer, locked, memberId, _configuredFeeds, cancellationToken).ConfigureAwait(false);
                if (!reconstructed.IsVerified || reconstructed.Candidate is null)
                    throw Refused($"Member '{memberId}' has no verifiable complete Active/LKG closure; supply source-bound graph evidence or keep enrollment Incomplete. {reconstructed.UnknownReasonCode}", ledger.RootIdentity);
                await reconstructed.RevalidateSourceStateAsync(locked, _serializer, cancellationToken).ConfigureAwait(false);
                reconstructed.RevalidateNative();
                candidate = reconstructed.Candidate;
                await locked.PublishStateAsync(memberId, candidate, cancellationToken).ConfigureAwait(false);
                reconstructed.RevalidateNative();
                results.Add(new(memberId, PackageStoreEnrollmentMemberDisposition.Enrolled, diagnostics));
                continue;
            }

            _ = PersistedStoreStateGraphVerifier.Verify(candidate);
            await locked.PublishStateAsync(memberId, candidate, cancellationToken).ConfigureAwait(false);
            results.Add(new(memberId, PackageStoreEnrollmentMemberDisposition.Enrolled, diagnostics));
        }

        var complete = await locked.CompleteEnrollmentAsync(cancellationToken).ConfigureAwait(false);
        return new PackageStoreEnrollmentOperationResult(request.RootLabel, complete.RootIdentity,
            complete.EnrollmentEpoch, complete.Status, results);
    }

    private async Task<PackageStoreEnrollmentOperationResult> MigrateAndCompleteAsync(
        RootMembershipRegistry.LockedMemberLocations locked,
        PackageStoreEnrollmentOperationRequest request,
        bool recoveringMigration,
        CancellationToken cancellationToken)
    {
        locked.RequirePayloadSerializer(_serializer);
        var ledger = locked.Ledger;
        var marker = ledger.PendingMembershipMigration
            ?? throw Refused("Migration requires the durable prior-roster marker.", ledger.RootIdentity);
        var prior = marker.PriorCompleteLedger;
        var expectedCurrentIds = request.ExpectedCurrentMemberIds.ToHashSet(StringComparer.Ordinal);
        var observedMigrationIds = recoveringMigration
            ? ledger.Members.Select(static member => member.MemberId).ToHashSet(StringComparer.Ordinal)
            : prior.Members.Select(static member => member.MemberId).ToHashSet(StringComparer.Ordinal);
        var expectedEpoch = recoveringMigration
            ? request.ExpectedEnrollmentEpoch : checked(request.ExpectedEnrollmentEpoch + 1);
        var expectedPriorEpoch = recoveringMigration
            ? checked(request.ExpectedEnrollmentEpoch - 1) : request.ExpectedEnrollmentEpoch;
        if (ledger.RootIdentity != request.ExpectedPhysicalRootIdentity ||
            ledger.EnrollmentEpoch != expectedEpoch || prior.EnrollmentEpoch != expectedPriorEpoch ||
            !ledger.TargetMemberIds.ToHashSet(StringComparer.Ordinal).SetEquals(request.IntendedMemberIds) ||
            !observedMigrationIds.SetEquals(expectedCurrentIds))
            throw Refused("The locked migration marker differs from the explicit root/epoch/old-new roster request.", ledger.RootIdentity);
        if (request.RetireLegacyRecoveryMemberIds.Any(memberId =>
                !request.IntendedMemberIds.Contains(memberId, StringComparer.Ordinal)))
            throw Refused("Legacy recovery retirement may name only intended target members.", ledger.RootIdentity);

        var results = new List<PackageStoreEnrollmentMemberResult>(
            request.IntendedMemberIds.Count + request.RetireMemberIds.Count);
        foreach (var memberId in request.IntendedMemberIds.OrderBy(static id => id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ledger = locked.Ledger;
            var member = ledger.Members.SingleOrDefault(candidate =>
                string.Equals(candidate.MemberId, memberId, StringComparison.Ordinal))
                ?? throw Refused($"Migration target '{memberId}' is absent from the exact locked union.", ledger.RootIdentity);
            if (!ledger.TargetMemberIds.Contains(memberId, StringComparer.Ordinal))
                throw Refused($"Migration target '{memberId}' is not in the persisted target set.", ledger.RootIdentity);

            var state = await locked.ReadMemberStateAsync(memberId, cancellationToken).ConfigureAwait(false);
            var diagnostics = new List<string>();
            var retireLegacy = request.RetireLegacyRecoveryMemberIds.Contains(memberId, StringComparer.Ordinal);
            var hasEvidence = TryGetEvidence(request, memberId, out var suppliedEvidence);
            StoreStateRecord candidate;
            if (member.Binding is RootMemberRecord.AcknowledgedBinding acknowledged)
            {
                if (state?.ProtectionRecord is not { } sourceProtection ||
                    !sourceProtection.HasSamePayloadAs(acknowledged.ProtectionRecord))
                    throw Refused($"Migration source '{memberId}' no longer matches its exact acknowledged protection.", ledger.RootIdentity);
                if (sourceProtection.EnrollmentEpoch == ledger.EnrollmentEpoch)
                {
                    if (retireLegacy && sourceProtection.LegacyRecoveryRetirementEvidence is null)
                        throw Refused($"Member '{memberId}' was already published without the requested legacy recovery retirement evidence.", ledger.RootIdentity);
                    // The migration union can still contain unacknowledged targets. The exact state/binding
                    // payload comparison above is authoritative for this member; verify its persisted graph here.
                    _ = PersistedStoreStateGraphVerifier.Verify(state);
                    results.Add(new(memberId, PackageStoreEnrollmentMemberDisposition.Recovered, []));
                    continue;
                }
                if (retireLegacy || hasEvidence)
                    throw Refused($"Member '{memberId}' is already protected by the prior roster; supplied legacy recovery retirement applies only to an unprotected source.", ledger.RootIdentity);
                candidate = PackageStoreProtectionRecordFactory.WithProtection(state, ledger.RootIdentity,
                    ledger.EnrollmentEpoch, memberId, checked(sourceProtection.Revision + 1),
                    sourceProtection.ActiveClosure, sourceProtection.RecoverableClosure,
                    sourceProtection.RetiredGraphs, sourceProtection.LegacyUnknownRecovery);
                diagnostics.Add("Rebound the verified member protection to the new membership epoch.");
            }
            else if (marker.MovedMemberIds.Contains(memberId, StringComparer.Ordinal))
            {
                if (retireLegacy || hasEvidence)
                    throw Refused($"Moved member '{memberId}' already has verified prior protection; legacy retirement evidence cannot replace that source.", ledger.RootIdentity);
                if (state is not null)
                    throw Refused($"Moved member target '{memberId}' is no longer absent.", ledger.RootIdentity);
                var sourceMember = prior.Members.Single(candidateMember =>
                    string.Equals(candidateMember.MemberId, memberId, StringComparison.Ordinal));
                var sourceProtection = (sourceMember.Binding as RootMemberRecord.AcknowledgedBinding)?.ProtectionRecord
                    ?? throw Refused($"Moved member '{memberId}' has no acknowledged prior source.", ledger.RootIdentity);
                var sourceState = await locked.ReadMigrationSourceStateAsync(memberId, cancellationToken).ConfigureAwait(false)
                    ?? throw Refused($"Moved member '{memberId}' lost its old source state.", ledger.RootIdentity);
                if (sourceState.ProtectionRecord is not { } actualSourceProtection ||
                    !actualSourceProtection.HasSamePayloadAs(sourceProtection))
                    throw Refused($"Moved member '{memberId}' no longer matches its exact old source protection.", ledger.RootIdentity);
                _ = PersistedStoreStateGraphVerifier.VerifyAcknowledgedMember(sourceState, prior, sourceMember,
                    ledger.RootIdentity, prior.EnrollmentEpoch, RootMembershipStatus.Complete);
                candidate = PackageStoreProtectionRecordFactory.WithProtection(sourceState, ledger.RootIdentity,
                    ledger.EnrollmentEpoch, memberId, checked(sourceProtection.Revision + 1),
                    sourceProtection.ActiveClosure, sourceProtection.RecoverableClosure,
                    sourceProtection.RetiredGraphs, sourceProtection.LegacyUnknownRecovery);
                diagnostics.Add("Published a verified copy at the declared target slot while retaining the old source through migration completion.");
            }
            else if (state is null)
            {
                if (retireLegacy || hasEvidence)
                    throw Refused($"Member '{memberId}' has no source state to bind supplied recovery evidence.", ledger.RootIdentity);
                var knownEmpty = KnownEmptyClosure();
                candidate = PackageStoreProtectionRecordFactory.WithProtection(StoreStateRecord.Empty(), ledger.RootIdentity,
                    ledger.EnrollmentEpoch, memberId, 1, knownEmpty, knownEmpty);
            }
            else if (member.Binding is RootMemberRecord.ExistingUnprotectedBinding)
            {
                if (state.ProtectionRecord is not null || state.ProtectionBundle is not null)
                    throw Refused($"New migration target '{memberId}' has protection not acknowledged by this marker.", ledger.RootIdentity);
                if (hasEvidence)
                {
                    candidate = BuildFromEvidence(ledger, memberId, state, locked.Locations[memberId], suppliedEvidence,
                        retireLegacy);
                    diagnostics.Add(retireLegacy
                        ? "Applied source-bound active graph evidence and recorded explicit retirement of legacy LKG entries."
                        : "Applied source-bound active/recoverable graph evidence after semantic and native verification.");
                }
                else if (retireLegacy)
                {
                    throw Refused($"Legacy recovery retirement for '{memberId}' requires source-bound graph evidence.", ledger.RootIdentity);
                }
                else if (IsKnownEmptyLegacy(state))
                {
                    var knownEmpty = KnownEmptyClosure();
                    candidate = PackageStoreProtectionRecordFactory.WithProtection(state, ledger.RootIdentity,
                        ledger.EnrollmentEpoch, memberId, 1, knownEmpty, knownEmpty);
                }
                else
                {
                    if (HasLegacyRecoveryBeyondActive(state))
                        throw Refused($"Member '{memberId}' contains LKG entries outside its active state; source-bound evidence and explicit retirement are required.", ledger.RootIdentity);
                    using var reconstructed = await LegacyActiveGraphCandidateProducer.CreateAsync(_files, _registry,
                        _serializer, locked, memberId, _configuredFeeds, cancellationToken).ConfigureAwait(false);
                    if (!reconstructed.IsVerified || reconstructed.Candidate is null)
                        throw Refused($"Member '{memberId}' has no verifiable complete Active/LKG closure. {reconstructed.UnknownReasonCode}", ledger.RootIdentity);
                    await reconstructed.RevalidateSourceStateAsync(locked, _serializer, cancellationToken).ConfigureAwait(false);
                    reconstructed.RevalidateNative();
                    candidate = reconstructed.Candidate;
                    await locked.PublishStateAsync(memberId, candidate, cancellationToken).ConfigureAwait(false);
                    reconstructed.RevalidateNative();
                    results.Add(new(memberId, PackageStoreEnrollmentMemberDisposition.Migrated, diagnostics));
                    continue;
                }
            }
            else
            {
                throw Refused($"Migration target '{memberId}' has an unsupported persisted binding.", ledger.RootIdentity);
            }

            _ = PersistedStoreStateGraphVerifier.Verify(candidate);
            await locked.PublishStateAsync(memberId, candidate, cancellationToken).ConfigureAwait(false);
            results.Add(new(memberId, PackageStoreEnrollmentMemberDisposition.Migrated, diagnostics));
        }

        foreach (var retiredMemberId in request.RetireMemberIds.OrderBy(static id => id, StringComparer.Ordinal))
            results.Add(new(retiredMemberId, PackageStoreEnrollmentMemberDisposition.Retired,
                ["Preserved the old state payload and recorded explicit membership retirement evidence."]));

        var complete = await locked.CompleteEnrollmentAsync(cancellationToken).ConfigureAwait(false);
        return new PackageStoreEnrollmentOperationResult(request.RootLabel, complete.RootIdentity,
            complete.EnrollmentEpoch, complete.Status, results);
    }

    private StoreStateRecord BuildFromEvidence(
        RootMembershipRecord ledger,
        string memberId,
        StoreStateRecord source,
        ResolvedMemberStateLocation location,
        PackageStoreMemberRecoveryEvidence evidence,
        bool retireLegacyRecovery)
    {
        if (source.ProtectionRecord is not null || source.ProtectionBundle is not null ||
            !string.Equals(memberId, evidence.MemberId, StringComparison.Ordinal) ||
            location.Slot != evidence.ExpectedStateSlot || location.ExistingFileIdentity != evidence.ExpectedStateFileIdentity ||
            !string.Equals(ProtectionDigest.StateBody(source), evidence.ExpectedStateBodyDigest, StringComparison.Ordinal))
            throw Refused($"Supplied graph evidence for member '{memberId}' does not bind the exact source state slot, native file identity and body digest.", ledger.RootIdentity);

        var active = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, evidence.ActiveGraphs);
        var recoverable = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, evidence.RecoverableGraphs);
        LegacyRecoveryRetirementEvidence? retirement = null;
        if (retireLegacyRecovery)
        {
            if (source.ProtectionRecord is not null || source.ProtectionBundle is not null ||
                location.ExistingFileIdentity is not { } sourceFileIdentity)
                throw Refused($"Legacy recovery retirement for member '{memberId}' requires one exact unprotected source file.", ledger.RootIdentity);
            var retiredEntries = source.LastKnownGoodById
                .Where(pair => !source.ActiveVersionById.TryGetValue(pair.Key, out var activeVersion) ||
                               !VersionsEqual(pair.Value, activeVersion))
                .ToArray();
            if (retiredEntries.Length == 0)
                throw Refused($"Member '{memberId}' has no legacy LKG entries outside its active state to retire.", ledger.RootIdentity);
            retirement = new LegacyRecoveryRetirementEvidence(memberId,
                ProtectionDigest.StateSlotIdentityDigest(location.Slot),
                ProtectionDigest.PhysicalFileIdentityDigest(sourceFileIdentity),
                evidence.ExpectedStateBodyDigest,
                ProtectionDigest.ActiveGraphClosureDigest(evidence.ActiveGraphs),
                retiredEntries,
                ledger.EnrollmentEpoch,
                1);
            source = source with
            {
                LastKnownGoodById = new Dictionary<string, string>(source.ActiveVersionById, StringComparer.OrdinalIgnoreCase)
            };
        }
        return PackageStoreProtectionRecordFactory.WithProtection(source, ledger.RootIdentity,
            ledger.EnrollmentEpoch, memberId, 1, active, recoverable,
            legacyRecoveryRetirementEvidence: retirement);
    }

    private static bool HasLegacyRecoveryBeyondActive(StoreStateRecord state)
        => state.LastKnownGoodById.Any(pair =>
            !state.ActiveVersionById.TryGetValue(pair.Key, out var activeVersion) || !VersionsEqual(pair.Value, activeVersion));

    private static bool VersionsEqual(string left, string right)
        => NuGet.Versioning.NuGetVersion.TryParse(left, out var leftVersion) &&
           NuGet.Versioning.NuGetVersion.TryParse(right, out var rightVersion) &&
           NuGet.Versioning.VersionComparer.VersionRelease.Equals(leftVersion, rightVersion);

    private static bool TryGetEvidence(
        PackageStoreEnrollmentOperationRequest request,
        string memberId,
        out PackageStoreMemberRecoveryEvidence evidence)
    {
        evidence = request.RecoveryEvidence.SingleOrDefault(item => string.Equals(item.MemberId, memberId, StringComparison.Ordinal))!;
        return evidence is not null;
    }

    private static PackageProtectionClosure KnownEmptyClosure()
        => new(PackageProtectionClosureKnowledge.Known, null, []);

    private static bool IsKnownEmptyLegacy(StoreStateRecord state)
        => state.ActiveVersionById.Count == 0 && state.LastKnownGoodById.Count == 0 &&
           state.ActiveGraphsByIdNormalized.Count == 0 && state.ActivePackageDescriptorsByIdNormalized.Count == 0;

    private static void ValidateUniqueDeclarations(IReadOnlyList<TrustedPackageStoreMemberDeclaration> declarations)
    {
        if (declarations.Select(static declaration => declaration.MemberId).Distinct(StringComparer.Ordinal).Count() != declarations.Count ||
            declarations.Select(static declaration => declaration.StateFileLocator).Distinct(StringComparer.Ordinal).Count() != declarations.Count)
            throw Refused("Trusted member IDs and configured state locators must each be unique within the selected root.");
    }

    private static PackageStoreAdmissionException Refused(string message, PhysicalRootIdentity? root = null)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message, root);
}
