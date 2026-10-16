using System.Collections.ObjectModel;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;

namespace Nuplane.Store.Coordination;

internal sealed partial class RootMembershipRegistry
{
    /// <summary>Owns root and old/new member locks for one new or resumed quiescent roster migration.</summary>
    internal async Task<TResult> WithQuiescentMembershipMigrationLocationsAsync<TResult>(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        long expectedPriorEpoch,
        IReadOnlyCollection<string> expectedCurrentMemberIds,
        IReadOnlyList<RootMemberRecord> declaredTargets,
        IReadOnlyCollection<string> retireMemberIds,
        PackageStoreAuthorityResolver authority,
        bool quiescentCutoverConfirmed,
        bool recoverExisting,
        Func<LockedMemberLocations, CancellationToken, Task<TResult>> callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(expectedRoot);
        ArgumentNullException.ThrowIfNull(expectedCurrentMemberIds);
        ArgumentNullException.ThrowIfNull(declaredTargets);
        ArgumentNullException.ThrowIfNull(retireMemberIds);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();
        if (!quiescentCutoverConfirmed)
            throw Refused("Membership migration requires explicit confirmation that every store user remains quiescent.");
        RequireRoot(root, expectedRoot);
        RequireEpoch(expectedPriorEpoch);

        var currentIds = expectedCurrentMemberIds.ToHashSet(StringComparer.Ordinal);
        var targets = declaredTargets.Select(static member => member.Copy()).ToArray();
        var targetIds = targets.Select(static member => member.MemberId).ToHashSet(StringComparer.Ordinal);
        var retireIds = retireMemberIds.ToHashSet(StringComparer.Ordinal);
        if (currentIds.Count != expectedCurrentMemberIds.Count || targetIds.Count != targets.Length ||
            targets.Length == 0 || targets.Any(static member => member.Binding is not RootMemberRecord.DeclaredBinding))
            throw Refused("Migration requires unique explicit old and target rosters and declared target locators.");

        var control = OpenControl(root);
        IAsyncDisposable? owner = null;
        Transaction? transaction = null;
        MemberLocatorReplayScope? sourceScope = null;
        MemberLocatorReplayScope? operationScope = null;
        IReadOnlyDictionary<string, ResolvedMemberStateLocation>? sourceLocations = null;
        IReadOnlyDictionary<string, ResolvedMemberStateLocation>? operationLocations = null;
        LockedMemberLocations? context = null;
        Dictionary<string, ResolvedMemberStateLocation>? bootstrapSourceLocations = null;
        Dictionary<string, ResolvedMemberStateLocation>? bootstrapTargetLocations = null;
        Dictionary<string, ResolvedMemberStateLocation>? bootstrapCurrentLocations = null;
        Dictionary<string, ResolvedMemberStateLocation>? bootstrapMovedSources = null;
        var bootstrapCurrentPolicy = LocatorReplayBindingPolicy.Enrollment;
        try
        {
            RootMembershipRecord? bootstrapLedger = null;
            RootMembershipRecord? sourceLedger = null;

            owner = await _locks.AcquireBootstrapAsync(control, token =>
            {
                token.ThrowIfCancellationRequested();
                var ledger = ReadLedger(root, control);
                var resuming = ledger.PendingMembershipMigration is not null;
                if (recoverExisting != resuming)
                    throw Refused(recoverExisting
                        ? "Recovery requires a durable pending membership migration marker."
                        : "A pending membership migration requires explicit recovery.");

                if (resuming)
                {
                    sourceLedger = ledger.PendingMembershipMigration!.PriorCompleteLedger;
                    ValidateMigrationRequest(sourceLedger, expectedRoot, expectedPriorEpoch, targetIds,
                        retireIds, targets);
                    if (!ledger.Members.Select(static member => member.MemberId).ToHashSet(StringComparer.Ordinal)
                            .SetEquals(currentIds))
                        throw Refused("Migration recovery must bind the exact observed Incomplete old/new member union.");
                    sourceScope = new MemberLocatorReplayScope(sourceLedger);
                    bootstrapMovedSources = ResolveMigrationSourceLocations(sourceLedger,
                        ledger.PendingMembershipMigration.MovedMemberIds, sourceScope, authority);
                    bootstrapCurrentPolicy = ledger.PendingStateCommit is null
                        ? LocatorReplayBindingPolicy.Enrollment
                        : LocatorReplayBindingPolicy.PendingStateRecovery;
                    operationScope = new MemberLocatorReplayScope(ledger,
                        allowPendingStateCommit: ledger.PendingStateCommit is not null);
                    bootstrapCurrentLocations = ResolveMemberLocatorMap(ledger, operationScope,
                        bootstrapCurrentPolicy).ToDictionary(static pair => pair.Key,
                            static pair => pair.Value, StringComparer.Ordinal);
                    foreach (var target in targets)
                    {
                        var persisted = ledger.Members.Single(member =>
                            string.Equals(member.MemberId, target.MemberId, StringComparison.Ordinal));
                        if (!string.Equals(persisted.ConfiguredLocator, target.ConfiguredLocator, StringComparison.Ordinal))
                            throw Refused("Recovery target declarations differ from the exact persisted migration locators.");
                    }
                    if (bootstrapCurrentLocations.Values.Any(location => bootstrapMovedSources.Values.Any(source => source.Slot == location.Slot)))
                        throw Refused("A moved source slot collides with the current target roster.");
                    bootstrapLedger = ledger;
                    var slots = bootstrapCurrentLocations.Values.Select(static location => location.Slot)
                        .Concat(bootstrapMovedSources.Values.Select(static location => location.Slot))
                        .Distinct().ToArray();
                    return Task.FromResult<IReadOnlyList<StateSlotIdentity>>(slots);
                }

                if (ledger.Status != RootMembershipStatus.Complete || ledger.RootIdentity != expectedRoot ||
                    ledger.EnrollmentEpoch != expectedPriorEpoch || ledger.PendingStateCommit is not null ||
                    ledger.PendingGroupPublicationV2 is not null)
                    throw Refused("A new migration requires the exact settled Complete root, epoch and old roster.");
                sourceLedger = ledger;
                ValidateMigrationRequest(sourceLedger, expectedRoot, expectedPriorEpoch, targetIds,
                    retireIds, targets);
                if (!sourceLedger.Members.Select(static member => member.MemberId).ToHashSet(StringComparer.Ordinal)
                        .SetEquals(currentIds))
                    throw Refused("A new migration must bind the exact observed prior Complete roster.");
                sourceScope = new MemberLocatorReplayScope(ledger);
                bootstrapSourceLocations = ResolveMemberLocatorMap(ledger, sourceScope,
                    LocatorReplayBindingPolicy.Acknowledged).ToDictionary(static pair => pair.Key,
                        static pair => pair.Value, StringComparer.Ordinal);
                var resolvedTargets = new Dictionary<string, ResolvedMemberStateLocation>(StringComparer.Ordinal);
                try
                {
                    foreach (var target in targets)
                        resolvedTargets.Add(target.MemberId, authority.ResolveMemberStateLocation(target.ConfiguredLocator, sourceScope));
                    var targetSlots = resolvedTargets.Values.Select(static location => location.Slot).ToArray();
                    if (targetSlots.Distinct().Count() != targetSlots.Length)
                        throw Refused("Distinct target declarations resolve to one native state slot.");
                    var oldSlotsByMember = bootstrapSourceLocations.ToDictionary(static pair => pair.Key,
                        static pair => pair.Value.Slot, StringComparer.Ordinal);
                    foreach (var target in resolvedTargets)
                    {
                        if (oldSlotsByMember.Any(old => !string.Equals(old.Key, target.Key, StringComparison.Ordinal) &&
                                old.Value == target.Value.Slot))
                            throw Refused("A target state slot aliases a different prior member's still-retained state slot.");
                    }
                    bootstrapTargetLocations = resolvedTargets;
                    bootstrapLedger = ledger;
                    var slots = bootstrapSourceLocations.Values.Select(static location => location.Slot)
                        .Concat(resolvedTargets.Values.Select(static location => location.Slot))
                        .Distinct().ToArray();
                    return Task.FromResult<IReadOnlyList<StateSlotIdentity>>(slots);
                }
                catch
                {
                    DisposeLocations(resolvedTargets.Values);
                    throw;
                }
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var observedLedger = bootstrapLedger ?? throw Refused("Migration bootstrap did not retain its exact source ledger.");
            var prior = sourceLedger ?? throw Refused("Migration bootstrap did not retain its prior Complete ledger.");
            var lockedLedger = ReadLedger(root, control, out var lockedLedgerIdentity);
            RequireSameDigest(observedLedger, lockedLedger);
            if (recoverExisting)
            {
                ValidateMigrationRequest(lockedLedger.PendingMembershipMigration!.PriorCompleteLedger,
                    expectedRoot, expectedPriorEpoch, targetIds, retireIds, targets);
                RevalidateMemberLocationMap(lockedLedger, bootstrapCurrentLocations!, bootstrapCurrentPolicy);
                RevalidateMigrationSourceMap(lockedLedger, prior, sourceScope, bootstrapMovedSources!);
                operationLocations = bootstrapCurrentLocations;
                bootstrapCurrentLocations = null;
                sourceLocations = bootstrapMovedSources;
                bootstrapMovedSources = null;
                operationScope = operationScope ?? new MemberLocatorReplayScope(lockedLedger);
            }
            else
            {
                RevalidateMemberLocationMap(prior, bootstrapSourceLocations!, LocatorReplayBindingPolicy.Acknowledged);
                foreach (var targetLocation in bootstrapTargetLocations!.Values)
                    targetLocation.Revalidate();
                var verifiedStates = await VerifyMigrationSourceRosterAsync(prior, root, expectedRoot,
                    bootstrapSourceLocations!, cancellationToken).ConfigureAwait(false);
                var migration = await BuildMigrationCandidateAsync(prior, targets, bootstrapTargetLocations,
                    verifiedStates, targetIds, retireIds, cancellationToken).ConfigureAwait(false);

                var transactionOwner = owner ?? throw Refused("Migration bootstrap did not retain its complete lock owner.");
                transaction = new Transaction(this, root, control, transactionOwner, lockedLedger, lockedLedgerIdentity);
                owner = null;
                transaction.Publish(migration);
                bootstrapLedger = migration;

                // Resolve the published target union against its new exact digest. Source handles for moved members
                // are independently resolved against the retained prior Complete ledger and stay unchanged.
                operationScope = new MemberLocatorReplayScope(migration);
                operationLocations = ResolveMemberLocatorMap(migration, operationScope,
                    LocatorReplayBindingPolicy.Enrollment);
                var moved = migration.PendingMembershipMigration!.MovedMemberIds;
                var movedSourceLocations = ResolveMigrationSourceLocations(prior, moved, sourceScope!, authority);
                sourceLocations = new ReadOnlyDictionary<string, ResolvedMemberStateLocation>(movedSourceLocations);
                DisposeLocations(bootstrapSourceLocations!.Values);
                DisposeLocations(bootstrapTargetLocations!.Values);
                bootstrapSourceLocations = null;
                bootstrapTargetLocations = null;
            }

            if (transaction is null)
            {
                var transactionOwner = owner ?? throw Refused("Migration recovery did not retain its complete lock owner.");
                transaction = new Transaction(this, root, control, transactionOwner, lockedLedger, lockedLedgerIdentity);
                owner = null;
            }
            if (transaction.ReadCurrent().PendingStateCommit is not null)
            {
                var pendingLocations = operationLocations
                    ?? throw Refused("Pending migration-state recovery has no retained member location map.");
                var parents = pendingLocations.ToDictionary(static pair => pair.Key,
                    static pair => pair.Value.Parent, StringComparer.Ordinal);
                _ = await RecoverAsync(transaction, parents, cancellationToken, checkpoint: null).ConfigureAwait(false);
                DisposeLocations(pendingLocations.Values);
                operationLocations = null;
                operationScope!.Expire();
                var recoveredLedger = transaction.ReadCurrent();
                operationScope = new MemberLocatorReplayScope(recoveredLedger);
                operationLocations = ResolveMemberLocatorMap(recoveredLedger, operationScope,
                    LocatorReplayBindingPolicy.Enrollment);
            }
            var marker = transaction.ReadCurrent().PendingMembershipMigration
                ?? throw Refused("The locked migration operation has no durable pending marker.");
            var movedSources = sourceLocations ?? new ReadOnlyDictionary<string, ResolvedMemberStateLocation>(
                new Dictionary<string, ResolvedMemberStateLocation>(StringComparer.Ordinal));
            var migrationSources = new MigrationSourceLocations(prior, sourceScope!, movedSources);
            context = CreateLockedMemberLocations(transaction, root, operationScope!, operationLocations!,
                LocatorReplayBindingPolicy.Enrollment, expectedRoot, checked(expectedPriorEpoch + 1),
                migrationSources: migrationSources);
            operationScope = null;
            operationLocations = null;
            sourceScope = null;
            sourceLocations = null;
            try
            {
                if (!ReferenceEquals(context.Ledger.PendingMembershipMigration, marker) &&
                    !string.Equals(context.Ledger.PendingMembershipMigration?.PriorCompleteLedger.LedgerDigest,
                        marker.PriorCompleteLedger.LedgerDigest, StringComparison.Ordinal))
                    throw Refused("The pending migration changed before its locked callback began.");
                return await callback(context, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                context.Dispose();
                context = null;
            }
        }
        finally
        {
            try
            {
                context?.Dispose();
                if (bootstrapSourceLocations is not null)
                    DisposeLocations(bootstrapSourceLocations.Values);
                if (bootstrapTargetLocations is not null)
                    DisposeLocations(bootstrapTargetLocations.Values);
                if (bootstrapCurrentLocations is not null)
                    DisposeLocations(bootstrapCurrentLocations.Values);
                if (bootstrapMovedSources is not null)
                    DisposeLocations(bootstrapMovedSources.Values);
                if (operationLocations is not null)
                    DisposeLocations(operationLocations.Values);
                if (sourceLocations is not null)
                    DisposeLocations(sourceLocations.Values);
            }
            finally
            {
                operationScope?.Expire();
                sourceScope?.Expire();
                try
                {
                    if (transaction is not null)
                        await transaction.DisposeAsync().ConfigureAwait(false);
                    else if (owner is not null)
                        await owner.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    if (transaction is null)
                        control.Dispose();
                }
            }
        }
    }

    internal static RootMembershipRecord CreateMigrationTargetProjection(RootMembershipRecord migrationLedger)
    {
        ArgumentNullException.ThrowIfNull(migrationLedger);
        if (migrationLedger.PendingMembershipMigration is null ||
            migrationLedger.Status != RootMembershipStatus.Incomplete ||
            migrationLedger.PendingStateCommit is not null)
            throw Refused("A migration target projection requires its settled Incomplete migration marker.", migrationLedger.RootIdentity);
        var targetMembers = migrationLedger.Members.Where(member =>
            migrationLedger.TargetMemberIds.Contains(member.MemberId, StringComparer.Ordinal)).ToArray();
        var schemaVersion = migrationLedger.PendingMembershipMigration.PriorCompleteLedger.SchemaVersion == RootMembershipRecord.BundleSchemaVersion
            ? RootMembershipRecord.BundleSchemaVersion : RootMembershipRecord.CurrentSchemaVersion;
        var projection = new RootMembershipRecord(schemaVersion, migrationLedger.RootIdentity,
            migrationLedger.EnrollmentEpoch, RootMembershipStatus.Incomplete, targetMembers,
            migrationLedger.TargetMemberIds, migrationLedger.RetiredMembers, null, PlaceholderDigest);
        return new RootMembershipRecord(projection.SchemaVersion, projection.RootIdentity,
            projection.EnrollmentEpoch, projection.Status, projection.Members,
            projection.TargetMemberIds, projection.RetiredMembers, null,
            ProtectionDigest.Ledger(projection));
    }

    private static void ValidateMigrationRequest(
        RootMembershipRecord prior,
        PhysicalRootIdentity expectedRoot,
        long expectedEpoch,
        IReadOnlySet<string> targetIds,
        IReadOnlySet<string> retireIds,
        IReadOnlyList<RootMemberRecord> targets)
    {
        var priorIds = prior.Members.Select(static member => member.MemberId).ToHashSet(StringComparer.Ordinal);
        var expectedRetire = priorIds.Except(targetIds, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (prior.Status != RootMembershipStatus.Complete || prior.RootIdentity != expectedRoot ||
            prior.EnrollmentEpoch != expectedEpoch ||
            !retireIds.SetEquals(expectedRetire))
            throw Refused("Migration must bind the exact prior Complete epoch/roster and explicitly retire every removed member.");
        var declarations = targets.ToDictionary(static member => member.MemberId, StringComparer.Ordinal);
        if (!declarations.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(targetIds))
            throw Refused("The target declaration set differs from the explicit intended roster.");
        if (prior.PendingStateCommit is not null || prior.PendingGroupPublicationV2 is not null ||
            prior.Members.Any(static member => member.Binding is not RootMemberRecord.AcknowledgedBinding))
            throw Refused("Migration requires every prior live member to retain an acknowledged Complete binding.");
    }

    private Dictionary<string, ResolvedMemberStateLocation> ResolveMigrationSourceLocations(
        RootMembershipRecord prior,
        IEnumerable<string> memberIds,
        MemberLocatorReplayScope scope,
        PackageStoreAuthorityResolver authority)
    {
        var result = new Dictionary<string, ResolvedMemberStateLocation>(StringComparer.Ordinal);
        try
        {
            foreach (var memberId in memberIds)
            {
                var member = prior.Members.SingleOrDefault(candidate =>
                    string.Equals(candidate.MemberId, memberId, StringComparison.Ordinal))
                    ?? throw Refused("A moved member source is absent from the prior Complete roster.");
                if (member.Binding is not RootMemberRecord.AcknowledgedBinding acknowledged)
                    throw Refused("A moved member source must retain its acknowledged Complete binding.");
                var location = authority.ResolveMemberStateLocation(member.ConfiguredLocator, scope);
                if (location.Slot != acknowledged.StateSlot || location.ExistingFileIdentity != acknowledged.ObservedStateFileIdentity)
                {
                    location.Dispose();
                    throw Refused("A moved member's old configured locator no longer resolves to its exact acknowledged source.");
                }
                if (!result.TryAdd(memberId, location))
                {
                    location.Dispose();
                    throw Refused("A moved source location was resolved more than once.");
                }
            }
            return result;
        }
        catch
        {
            DisposeLocations(result.Values);
            throw;
        }
    }

    private async Task<IReadOnlyDictionary<string, StoreStateRecord>> VerifyMigrationSourceRosterAsync(
        RootMembershipRecord prior,
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        IReadOnlyDictionary<string, ResolvedMemberStateLocation> locations,
        CancellationToken cancellationToken)
    {
        var scope = new MemberLocatorReplayScope(prior);
        try
        {
            void Revalidate()
            {
                scope.RequireCandidate(prior.RootIdentity, prior);
                RevalidateMemberLocationMap(prior, locations, LocatorReplayBindingPolicy.Acknowledged);
            }
            async Task<StoreStateRecord?> Read(string memberId, CancellationToken token)
            {
                Revalidate();
                var member = FindMember(prior, memberId);
                var state = await ReadPriorAsync(member.Binding, locations[memberId].Parent, token).ConfigureAwait(false);
                Revalidate();
                return state?.State;
            }
            return await VerifyAllMemberProtectionCoreAsync(prior, root, expectedRoot, prior.EnrollmentEpoch,
                RootMembershipStatus.Complete, Read, Revalidate, cancellationToken).ConfigureAwait(false);
        }
        finally { scope.Expire(); }
    }

    private async Task<RootMembershipRecord> BuildMigrationCandidateAsync(
        RootMembershipRecord prior,
        IReadOnlyList<RootMemberRecord> declarations,
        IReadOnlyDictionary<string, ResolvedMemberStateLocation> targetLocations,
        IReadOnlyDictionary<string, StoreStateRecord> verifiedPriorStates,
        IReadOnlySet<string> targetIds,
        IReadOnlySet<string> retireIds,
        CancellationToken cancellationToken)
    {
        var priorById = prior.Members.ToDictionary(static member => member.MemberId, StringComparer.Ordinal);
        var declarationsById = declarations.ToDictionary(static member => member.MemberId, StringComparer.Ordinal);
        var targetBindings = new Dictionary<string, RootMemberRecord>(StringComparer.Ordinal);
        var moved = new List<string>();
        foreach (var targetId in targetIds)
        {
            var declaration = declarationsById[targetId];
            var location = targetLocations[targetId];
            if (priorById.TryGetValue(targetId, out var previous))
            {
                var previousBinding = previous.Binding as RootMemberRecord.AcknowledgedBinding
                    ?? throw Refused("A retained migration source lost its prior acknowledgement.");
                if (location.Slot == previousBinding.StateSlot)
                {
                    if (location.ExistingFileIdentity != previousBinding.ObservedStateFileIdentity)
                        throw Refused("An unchanged migration target no longer has its exact acknowledged state file.");
                    targetBindings.Add(targetId, new RootMemberRecord(targetId, declaration.ConfiguredLocator,
                        new RootMemberRecord.AcknowledgedBinding(previousBinding.StateSlot,
                            previousBinding.ObservedStateFileIdentity, previousBinding.ProtectionRecord)));
                }
                else
                {
                    if (location.ExistingFileIdentity is not null)
                        throw Refused("A moved member target must be absent; migration will publish verified source state into that slot.");
                    targetBindings.Add(targetId, new RootMemberRecord(targetId, declaration.ConfiguredLocator,
                        new RootMemberRecord.ProspectiveBinding(location.Slot.ParentIdentity,
                            location.Slot.NameSemantics, location.RequestedBasename)));
                    moved.Add(targetId);
                }
            }
            else if (location.ExistingFileIdentity is not { } fileIdentity)
            {
                targetBindings.Add(targetId, new RootMemberRecord(targetId, declaration.ConfiguredLocator,
                    new RootMemberRecord.ProspectiveBinding(location.Slot.ParentIdentity,
                        location.Slot.NameSemantics, location.RequestedBasename)));
            }
            else
            {
                var existing = await ReadStateAsync(location.Parent, location.Slot, fileIdentity,
                    cancellationToken).ConfigureAwait(false);
                if (existing.State.ProtectionRecord is not null || existing.State.ProtectionBundle is not null)
                    throw Refused("A new migration target contains protected state that is not acknowledged by the source ledger.");
                targetBindings.Add(targetId, new RootMemberRecord(targetId, declaration.ConfiguredLocator,
                    new RootMemberRecord.ExistingUnprotectedBinding(location.Slot, existing.Identity,
                        ProtectionDigest.StateBody(existing.State), protectionMetadataAbsent: true)));
            }
        }

        var union = new List<RootMemberRecord>(prior.Members.Count + targetBindings.Count);
        foreach (var oldMember in prior.Members)
            union.Add(targetBindings.TryGetValue(oldMember.MemberId, out var target) ? target : oldMember.Copy());
        foreach (var added in targetBindings.Where(pair => !priorById.ContainsKey(pair.Key)))
            union.Add(added.Value);

        var newEpoch = checked(prior.EnrollmentEpoch + 1);
        var retired = new List<RootMemberRetirementEvidence>();
        foreach (var inherited in prior.RetiredMembers)
        {
            var bodyDigest = inherited.PriorBinding.ProtectionRecord.StateBodyDigest;
            retired.Add(new RootMemberRetirementEvidence(inherited.MemberId, inherited.PriorBinding, newEpoch,
                ProtectionDigest.MemberRetirementProof(prior.RootIdentity, inherited.MemberId, newEpoch,
                    prior.LedgerDigest, bodyDigest)));
        }
        foreach (var memberId in retireIds)
        {
            var member = priorById[memberId];
            var binding = (RootMemberRecord.AcknowledgedBinding)member.Binding;
            var state = verifiedPriorStates[memberId];
            retired.Add(new RootMemberRetirementEvidence(memberId, binding, newEpoch,
                ProtectionDigest.MemberRetirementProof(prior.RootIdentity, memberId, newEpoch,
                    prior.LedgerDigest, ProtectionDigest.StateBody(state))));
        }

        return CreateMigrationIncomplete(prior, union, targetIds, retired, moved);
    }

    private async Task<RootMembershipRecord> CompleteMembershipMigrationUnderOwnerAsync(
        Transaction transaction,
        PhysicalStoreDirectoryHandle root,
        LockedMemberLocations context,
        PhysicalRootIdentity expectedRoot,
        long expectedEpoch,
        RootMembershipRecord prior,
        CancellationToken cancellationToken)
    {
        var ledger = transaction.ReadCurrent();
        if (ledger.PendingMembershipMigration is not { } marker ||
            !string.Equals(marker.PriorCompleteLedger.LedgerDigest, prior.LedgerDigest, StringComparison.Ordinal) ||
            ledger.RootIdentity != expectedRoot || ledger.EnrollmentEpoch != expectedEpoch ||
            ledger.Status != RootMembershipStatus.Incomplete || ledger.PendingStateCommit is not null ||
            ledger.Members.Where(member => ledger.TargetMemberIds.Contains(member.MemberId, StringComparer.Ordinal))
                .Any(static member => member.Binding is not RootMemberRecord.AcknowledgedBinding))
            throw Refused("Migration completion requires its exact pending marker and fully acknowledged target roster.");

        void Revalidate()
        {
            var current = transaction.ReadCurrent();
            if (current.PendingMembershipMigration is not { } currentMarker ||
                !string.Equals(currentMarker.PriorCompleteLedger.LedgerDigest, prior.LedgerDigest, StringComparison.Ordinal))
                throw Refused("The pending migration source changed during completion.");
            context.Revalidate();
        }
        async Task<StoreStateRecord?> ReadTarget(string memberId, CancellationToken token)
        {
            Revalidate();
            var location = context.Locations.TryGetValue(memberId, out var found)
                ? found : throw Refused("A migration target has no retained locked state location.");
            var state = await ReadMemberStateAsync(transaction, memberId, location.Parent, token).ConfigureAwait(false);
            Revalidate();
            return state;
        }

        var targetProjection = CreateMigrationTargetProjection(ledger);
        var targetMembers = targetProjection.Members;
        await VerifyAllMemberProtectionCoreAsync(targetProjection, root, expectedRoot, expectedEpoch,
            RootMembershipStatus.Incomplete, ReadTarget, Revalidate, cancellationToken,
            authorityLedger: ledger).ConfigureAwait(false);

        var sourceScope = new MemberLocatorReplayScope(ledger);
        var sourceObservations = new List<IDisposable>();
        var sourceRevalidations = new List<Action>();
        try
        {
            foreach (var oldMember in prior.Members.Where(member =>
                         marker.MovedMemberIds.Contains(member.MemberId, StringComparer.Ordinal) ||
                         ledger.RetiredMembers.Any(retired => string.Equals(retired.MemberId, member.MemberId, StringComparison.Ordinal))))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (oldMember.Binding is not RootMemberRecord.AcknowledgedBinding oldBinding)
                    throw Refused("A moved or retired source lost its prior Complete acknowledgement.");
                StoreStateRecord? sourceState;
                if (marker.MovedMemberIds.Contains(oldMember.MemberId, StringComparer.Ordinal))
                {
                    sourceState = await ReadMigrationSourceStateAsync(transaction, prior,
                        context.MigrationSourceLocations, oldMember.MemberId, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var location = context.Locations.TryGetValue(oldMember.MemberId, out var found)
                        ? found : throw Refused("A retired source has no retained locked state location.");
                    sourceState = (await ReadPriorAsync(oldBinding, location.Parent, cancellationToken).ConfigureAwait(false))?.State;
                }
                if (sourceState is null)
                    throw Refused("A moved or retired member's original state disappeared before completion.");
                var graphs = PersistedStoreStateGraphVerifier.VerifyAcknowledgedMember(sourceState, prior, oldMember,
                    expectedRoot, prior.EnrollmentEpoch, RootMembershipStatus.Complete);
                if (sourceState.ProtectionRecord is not { } protection || !protection.HasSamePayloadAs(oldBinding.ProtectionRecord))
                    throw Refused("A moved or retired member's original source changed during migration.");
                ObserveProtectedInstalls(root, expectedRoot, sourceState,
                    graphs.ActiveGraphs.Concat(graphs.RecoverableGraphs), sourceScope,
                    sourceObservations, sourceRevalidations, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Revalidate();
            foreach (var revalidate in sourceRevalidations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                revalidate();
            }
            Revalidate();

            var final = new RootMembershipRecord(targetProjection.SchemaVersion, expectedRoot, expectedEpoch,
                RootMembershipStatus.Complete, targetMembers, ledger.TargetMemberIds, ledger.RetiredMembers,
                pendingStateCommit: null, ledgerDigest: PlaceholderDigest);
            final = new RootMembershipRecord(final.SchemaVersion, final.RootIdentity, final.EnrollmentEpoch,
                final.Status, final.Members, final.TargetMemberIds, final.RetiredMembers, null,
                ProtectionDigest.Ledger(final));
            transaction.Publish(final);
            return transaction.Ledger;
        }
        finally
        {
            try
            {
                for (var index = sourceObservations.Count - 1; index >= 0; index--)
                    sourceObservations[index].Dispose();
            }
            finally { sourceScope.Expire(); }
        }
    }
}
