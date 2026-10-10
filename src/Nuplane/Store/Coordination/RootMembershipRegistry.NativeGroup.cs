using System.Text.Json;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;

namespace Nuplane.Store.Coordination;

internal sealed partial class RootMembershipRegistry
{
    internal enum NativeGroupPublicationPoint
    {
        IntentPublished,
        StageFlushed,
        BackupFlushed,
        ArtifactsBound,
        StatePublished,
        StateVerified,
        ResolutionPublished,
        PriorStageRemoved,
        PriorBackupRemoved,
        PriorLedgerRestored,
        Acknowledged
    }

    internal sealed record NativeGroupRootRequest(
        PhysicalStoreDirectoryHandle RootHandle,
        PhysicalRootIdentity RootIdentity,
        long EnrollmentEpoch);

    /// <summary>
    /// Publishes one immutable bundle through retained root-local ledger transactions and one held shared
    /// state parent. This remains an internal primitive; callers must separately provide the selected roots.
    /// </summary>
    internal async Task<IReadOnlyList<RootMembershipRecord>> PublishNativeGroupAsync(
        GroupPublicationDescriptorV2 descriptor,
        IReadOnlyList<NativeGroupRootRequest> roots,
        StoreStateRecord nextState,
        CancellationToken cancellationToken,
        Action<NativeGroupPublicationPoint, PhysicalRootIdentity?>? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(nextState);
        await using var group = await AcquireNativeGroupOwnerAsync(descriptor, roots, cancellationToken).ConfigureAwait(false);
        await group.RequireAllPriorAsync(cancellationToken).ConfigureAwait(false);
        var bundle = RequireNextBundle(descriptor, nextState);
        group.VerifyNextBundleAndInstalls(bundle, nextState, cancellationToken);
        var nextBytes = await EncodeGroupStateAsync(nextState, cancellationToken).ConfigureAwait(false);

        foreach (var participant in group.Participants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ledger = participant.Transaction!.ReadCurrent();
            participant.Transaction!.Publish(BuildGroupLedger(ledger, RootMembershipStatus.Incomplete,
                ledger.Members, new PendingGroupPublicationV2(descriptor, participant.Request.RootIdentity,
                    GroupPublicationPhaseV2.Intent, null, null, GroupPublicationResolutionV2.Unresolved)));
            checkpoint?.Invoke(NativeGroupPublicationPoint.IntentPublished, participant.Request.RootIdentity);
        }

        group.RefreshMemberLocatorScopes();
        var sharedForPreparation = group.SharedParent;
        var stageName = descriptor.Participants[0].StagedName;
        var backupName = descriptor.Participants[0].BackupName;
        RequireAbsent(sharedForPreparation, stageName);
        if (backupName is not null)
            RequireAbsent(sharedForPreparation, backupName);
        var stagedIdentity = CreateFile(sharedForPreparation, stageName, nextBytes);
        await RequireGroupBundleArtifactAsync(sharedForPreparation, stageName, stagedIdentity, descriptor, nextState,
            cancellationToken).ConfigureAwait(false);
        RequireProfile(sharedForPreparation, stageName, stagedIdentity, descriptor.SharedStateSlot);
        checkpoint?.Invoke(NativeGroupPublicationPoint.StageFlushed, null);

        PhysicalFileIdentity? backupIdentity = null;
        if (descriptor.Participants[0].PriorStateFileIdentity is not null)
        {
            var prior = await group.ReadAndVerifyPriorAsync(cancellationToken).ConfigureAwait(false)
                ?? throw Refused("A bound prior identity requires a verified prior payload.");
            backupIdentity = CreateFile(sharedForPreparation, backupName!, prior.Bytes);
            await group.RequirePriorArtifactAsync(backupName!, backupIdentity, cancellationToken).ConfigureAwait(false);
            checkpoint?.Invoke(NativeGroupPublicationPoint.BackupFlushed, null);
        }

        foreach (var participant in group.Participants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = participant.Transaction!.ReadCurrent();
            var pending = current.PendingGroupPublicationV2
                ?? throw Refused("A group Intent disappeared before artifact binding.");
            participant.Transaction!.Publish(BuildGroupLedger(current, RootMembershipStatus.Incomplete,
                current.Members, pending.BindArtifacts(stagedIdentity, backupIdentity)));
            checkpoint?.Invoke(NativeGroupPublicationPoint.ArtifactsBound, participant.Request.RootIdentity);
        }

        group.RefreshMemberLocatorScopes();
        await group.ReadAndVerifyPriorAsync(cancellationToken).ConfigureAwait(false);
        var shared = group.SharedParent;
        _publication.PublishControlFileAt(shared, stageName, stagedIdentity,
            descriptor.SharedStateSlot.CanonicalBasename, descriptor.Participants[0].PriorStateFileIdentity);
        checkpoint?.Invoke(NativeGroupPublicationPoint.StatePublished, null);
        var actual = await ReadGroupStateAsync(shared, descriptor.SharedStateSlot, stagedIdentity,
            cancellationToken).ConfigureAwait(false);
        RequireExactGroupBundle(actual.State, descriptor, nextState);
        group.VerifyNextBundleAndInstalls(bundle, actual.State, cancellationToken);
        checkpoint?.Invoke(NativeGroupPublicationPoint.StateVerified, null);

        foreach (var participant in group.Participants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = participant.Transaction!.ReadCurrent();
            var pending = current.PendingGroupPublicationV2
                ?? throw Refused("A group BoundCommit disappeared before resolution.");
            if (pending.Phase != GroupPublicationPhaseV2.ArtifactsBound || pending.StagedStateFileIdentity != stagedIdentity ||
                pending.BackupStateFileIdentity != backupIdentity)
                throw Refused("Every participant must retain the exact same bound group artifacts.");
        }
        foreach (var participant in group.Participants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = participant.Transaction!.ReadCurrent();
            var pending = current.PendingGroupPublicationV2!;
            participant.Transaction!.Publish(BuildGroupLedger(current, RootMembershipStatus.Incomplete,
                current.Members, pending.Resolve(GroupPublicationResolutionV2.Next)));
            checkpoint?.Invoke(NativeGroupPublicationPoint.ResolutionPublished, participant.Request.RootIdentity);
        }

        group.RefreshMemberLocatorScopes();
        if (backupIdentity is not null)
        {
            await group.RequirePriorArtifactAsync(backupName!, backupIdentity, cancellationToken).ConfigureAwait(false);
            RemoveExactArtifact(group.SharedParent, backupName!, backupIdentity);
        }
        foreach (var participant in group.Participants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = participant.Transaction!.ReadCurrent();
            var pending = current.PendingGroupPublicationV2
                ?? throw Refused("A Next resolution disappeared before Complete acknowledgement.");
            if (pending.Resolution != GroupPublicationResolutionV2.Next || pending.Phase != GroupPublicationPhaseV2.Resolved)
                throw Refused("Complete acknowledgement requires every local Next resolution.");
            var member = current.Members.Single(item => item.MemberId == pending.LocalParticipant.PriorMember.MemberId);
            var row = bundle.Rows.Single(item => item.RootIdentity == participant.Request.RootIdentity);
            var binding = new RootMemberRecord.BundleAcknowledgedBinding(descriptor.SharedStateSlot, stagedIdentity,
                descriptor.LogicalMemberId, descriptor.ParticipantSetDigest, bundle.PublicationId,
                bundle.StateGeneration, bundle.StateBodyDigest, bundle.BundleDigest, row);
            var members = current.Members.Select(item => item.MemberId == member.MemberId
                ? new RootMemberRecord(item.MemberId, item.ConfiguredLocator, binding)
                : item).ToArray();
            participant.Transaction!.Publish(BuildGroupLedger(current, RootMembershipStatus.Complete, members, null));
            checkpoint?.Invoke(NativeGroupPublicationPoint.Acknowledged, participant.Request.RootIdentity);
        }
        return group.Participants.Select(static participant => participant.Transaction!.Ledger).ToArray();
    }

    /// <summary>Resumes only a single already-durable group decision; ambiguous evidence stays untouched.</summary>
    internal async Task<IReadOnlyList<RootMembershipRecord>> RecoverNativeGroupAsync(
        GroupPublicationDescriptorV2 descriptor,
        IReadOnlyList<NativeGroupRootRequest> roots,
        CancellationToken cancellationToken,
        Action<NativeGroupPublicationPoint, PhysicalRootIdentity?>? checkpoint = null)
    {
        await using var group = await AcquireNativeGroupOwnerAsync(descriptor, roots, cancellationToken).ConfigureAwait(false);
        var beforeIntent = group.Participants.Select(participant => participant.Transaction!.ReadCurrent()).ToArray();
        if (beforeIntent.All(static ledger => ledger.PendingGroupPublicationV2 is null))
        {
            if (beforeIntent.All(static ledger => ledger.Status == RootMembershipStatus.Complete &&
                    ledger.SchemaVersion == RootMembershipRecord.BundleSchemaVersion))
                return await group.ValidateAcknowledgedNextAsync(cancellationToken).ConfigureAwait(false);
            if (beforeIntent.All(ledger => ledger.Status == descriptor.GetParticipant(ledger.RootIdentity).PriorMembershipStatus &&
                    ledger.SchemaVersion == descriptor.GetParticipant(ledger.RootIdentity).PriorSchemaVersion &&
                    ledger.LedgerDigest == descriptor.GetParticipant(ledger.RootIdentity).PriorLedgerDigest))
            {
                var current = await group.TryReadCurrentGroupStateAsync(cancellationToken).ConfigureAwait(false);
                if (!await group.IsExactPriorStateAsync(current, cancellationToken).ConfigureAwait(false))
                    throw Refused("The completed Prior recovery no longer has its exact pre-intent state installed.");
                group.RequirePlannedArtifactsAbsent();
                return beforeIntent;
            }
            throw Refused("No persisted group transaction is available for recovery.");
        }
        if (beforeIntent.Any(static ledger => ledger.PendingGroupPublicationV2 is not null))
        {
            // A mixed Intent/prior prefix must prove the prior v2 graph against current native installs
            // before filling any missing Intent row. Otherwise refusal could itself mutate a participant ledger.
            var currentPrior = await group.TryReadCurrentGroupStateAsync(cancellationToken).ConfigureAwait(false);
            await group.IsExactPriorStateAsync(currentPrior, cancellationToken).ConfigureAwait(false);
        }
        await group.EnsureIntentAcrossGroupAsync(cancellationToken, checkpoint).ConfigureAwait(false);
        var pendingRecords = group.Participants.Select(participant => participant.Transaction!.ReadCurrent()).ToArray();
        var groupPending = pendingRecords.Select(static ledger => ledger.PendingGroupPublicationV2)
            .Where(static pending => pending is not null).Cast<PendingGroupPublicationV2>().ToArray();
        if (groupPending.Length == 0)
        {
            if (pendingRecords.All(ledger => ledger.Status == RootMembershipStatus.Complete &&
                    ledger.SchemaVersion == RootMembershipRecord.BundleSchemaVersion))
                return await group.ValidateAcknowledgedNextAsync(cancellationToken).ConfigureAwait(false);
            throw Refused("No persisted group transaction is available for recovery.");
        }

        var resolutions = groupPending.Select(static pending => pending.Resolution)
            .Where(static resolution => resolution != GroupPublicationResolutionV2.Unresolved).Distinct().ToArray();
        if (resolutions.Length > 1)
            throw Refused("Conflicting group resolutions require preserving all participant state and artifacts.");
        var resolution = resolutions.SingleOrDefault();
        var localPending = groupPending[0];
        if (groupPending.Any(pending => pending.Descriptor.IntentDigest != descriptor.IntentDigest))
            throw Refused("The participant ledgers do not retain the same immutable group intent.");

        if (resolution == GroupPublicationResolutionV2.Next)
        {
            await group.RequireBoundArtifactsConsistentAsync(cancellationToken, allowMissingBackup: true).ConfigureAwait(false);
            var state = await group.ReadCurrentGroupStateAsync(cancellationToken).ConfigureAwait(false);
            var bundle = RequireNextBundle(descriptor, state.State);
            RequireExactGroupBundle(state.State, descriptor, state.State);
            group.VerifyNextBundleAndInstalls(bundle, state.State, cancellationToken);
            var wroteNextResolution = false;
            foreach (var participant in group.Participants)
            {
                var current = participant.Transaction!.ReadCurrent();
                if (current.PendingGroupPublicationV2 is { Resolution: GroupPublicationResolutionV2.Unresolved } pending)
                {
                    participant.Transaction!.Publish(BuildGroupLedger(current, RootMembershipStatus.Incomplete,
                        current.Members, pending.Resolve(GroupPublicationResolutionV2.Next)));
                    wroteNextResolution = true;
                }
                checkpoint?.Invoke(NativeGroupPublicationPoint.ResolutionPublished, participant.Request.RootIdentity);
            }
            if (wroteNextResolution)
                group.RefreshMemberLocatorScopes();
            var backupName = descriptor.Participants[0].BackupName;
            if (backupName is not null && localPending.BackupStateFileIdentity is { } backup)
                RemoveExactArtifactIfPresent(group.SharedParent, backupName, backup);
            return await group.AcknowledgeNextAsync(bundle, state.Identity, checkpoint, cancellationToken).ConfigureAwait(false);
        }

        var anyNext = pendingRecords.Any(ledger => ledger.PendingGroupPublicationV2?.Resolution == GroupPublicationResolutionV2.Next);
        if (anyNext)
            throw Refused("A durable Next prefix forbids a Prior resolution.");
        var currentState = await group.TryReadCurrentGroupStateAsync(cancellationToken).ConfigureAwait(false);
        var bound = groupPending.Where(static pending => pending.StagedStateFileIdentity is not null)
            .ToArray();
        var priorAlreadyDecided = groupPending.Any(static pending => pending.Resolution == GroupPublicationResolutionV2.Prior);
        var priorInstalled = await group.IsExactPriorStateAsync(currentState, cancellationToken).ConfigureAwait(false);
        if (priorInstalled)
        {
            if (priorAlreadyDecided)
            {
                if (bound.Length > 0)
                {
                    group.RequireConsistentBoundTuple(bound);
                    if (groupPending.Any(static pending => pending.Resolution == GroupPublicationResolutionV2.Next ||
                            pending.Resolution == GroupPublicationResolutionV2.Unresolved &&
                            pending.Phase is not (GroupPublicationPhaseV2.Intent or GroupPublicationPhaseV2.ArtifactsBound)))
                        throw Refused("A durable Prior prefix has a conflicting participant phase.");
                }
                else
                {
                    group.RequirePlannedArtifactsAbsent();
                }
                if (bound.Length > 0 && groupPending.Any(static pending =>
                        pending.Resolution == GroupPublicationResolutionV2.Unresolved))
                {
                    await group.BindIntentRecordsToPriorDecisionAsync(bound[0], cancellationToken, checkpoint).ConfigureAwait(false);
                }
            }
            else if (bound.Length == 0)
                group.RequirePlannedArtifactsAbsent();
            else
            {
                group.RequireConsistentBoundTuple(bound);
                await group.RequireBoundArtifactLocationsAsync(bound[0], cancellationToken).ConfigureAwait(false);
                await group.BindIntentRecordsToObservedArtifactsAsync(bound[0], cancellationToken, checkpoint).ConfigureAwait(false);
            }
        }
        else
        {
            if (bound.Length == 0)
                throw Refused("The installed payload is not the exact prior state and no bound Next evidence exists.");
            group.RequireConsistentBoundTuple(bound);
            var installed = currentState
                ?? throw Refused("The shared state slot is absent and cannot prove the bound Next payload.");
            var installedBundle = RequireNextBundle(descriptor, installed.State);
            if (installed.Identity != bound[0].StagedStateFileIdentity)
                throw Refused("An unmarked state replacement does not match the exact bound Next artifact identity.");
            group.VerifyNextBundleAndInstalls(installedBundle, installed.State, cancellationToken);
            await group.RequireBoundArtifactLocationsAsync(bound[0], cancellationToken).ConfigureAwait(false);
            await group.BindIntentRecordsToObservedArtifactsAsync(bound[0], cancellationToken, checkpoint).ConfigureAwait(false);
            foreach (var participant in group.Participants)
            {
                var current = participant.Transaction!.ReadCurrent();
                var pending = current.PendingGroupPublicationV2!;
                participant.Transaction.Publish(BuildGroupLedger(current, RootMembershipStatus.Incomplete,
                    current.Members, pending.Resolve(GroupPublicationResolutionV2.Next)));
                checkpoint?.Invoke(NativeGroupPublicationPoint.ResolutionPublished, participant.Request.RootIdentity);
            }
            group.RefreshMemberLocatorScopes();
            if (bound[0].BackupStateFileIdentity is { } backupIdentity)
            {
                var backupName = bound[0].LocalParticipant.BackupName!;
                await group.RequirePriorArtifactAsync(backupName, backupIdentity, cancellationToken).ConfigureAwait(false);
                RemoveExactArtifact(group.SharedParent, backupName, backupIdentity);
            }
            return await group.AcknowledgeNextAsync(installedBundle, installed.Identity, checkpoint,
                cancellationToken).ConfigureAwait(false);
        }
        foreach (var participant in group.Participants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = participant.Transaction!.ReadCurrent();
            var pending = current.PendingGroupPublicationV2
                ?? throw Refused("A Prior decision cannot omit a participant's durable group Intent.");
            if (pending.Resolution == GroupPublicationResolutionV2.Unresolved)
            {
                if (priorAlreadyDecided && bound.Length > 0 && pending.Phase == GroupPublicationPhaseV2.Intent)
                    pending = pending.BindArtifacts(bound[0].StagedStateFileIdentity!, bound[0].BackupStateFileIdentity);
                participant.Transaction!.Publish(BuildGroupLedger(current, RootMembershipStatus.Incomplete,
                    current.Members, pending.Resolve(GroupPublicationResolutionV2.Prior)));
            }
            else if (pending.Resolution != GroupPublicationResolutionV2.Prior)
                throw Refused("A durable Next decision forbids a Prior resolution.");
            checkpoint?.Invoke(NativeGroupPublicationPoint.ResolutionPublished, participant.Request.RootIdentity);
        }
        var priorDecision = group.Participants.Select(participant => participant.Transaction!.ReadCurrent().PendingGroupPublicationV2)
            .ToArray();
        if (priorDecision.Any(static pending => pending is null || pending.Phase != GroupPublicationPhaseV2.Resolved ||
                pending.Resolution != GroupPublicationResolutionV2.Prior) ||
            priorDecision.Select(static pending => pending!.Descriptor.IntentDigest).Distinct(StringComparer.Ordinal).Count() != 1 ||
            priorDecision.Select(static pending => pending!.BoundCommitDigest).Distinct(StringComparer.Ordinal).Count() != 1 ||
            priorDecision.Select(static pending => pending!.ResolutionDigest).Distinct(StringComparer.Ordinal).Count() != 1)
            throw Refused("Every participant must persist and reopen the identical Prior resolution before artifact cleanup.");
        group.RefreshMemberLocatorScopes();
        var resolvedBound = priorDecision.Where(static pending => pending!.StagedStateFileIdentity is not null)
            .Cast<PendingGroupPublicationV2>().ToArray();
        if (resolvedBound.Length > 0)
        {
            group.RequireConsistentBoundTuple(resolvedBound);
            await group.RemoveBoundArtifactsAfterPriorDecisionAsync(resolvedBound[0], cancellationToken, checkpoint).ConfigureAwait(false);
        }
        else
        {
            group.RequirePlannedArtifactsAbsent();
        }
        foreach (var participant in group.Participants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = participant.Transaction!.ReadCurrent();
            var pending = current.PendingGroupPublicationV2
                ?? throw Refused("A Prior decision disappeared before restoring the exact prior ledger.");
            participant.Transaction!.Publish(group.ReconstructPriorLedger(current, pending.LocalParticipant));
            checkpoint?.Invoke(NativeGroupPublicationPoint.PriorLedgerRestored, participant.Request.RootIdentity);
        }
        return group.Participants.Select(static participant => participant.Transaction!.Ledger).ToArray();
    }

    private async Task<NativeGroupPublicationOwner> AcquireNativeGroupOwnerAsync(
        GroupPublicationDescriptorV2 descriptor,
        IReadOnlyList<NativeGroupRootRequest> requests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count != descriptor.Participants.Count)
            throw new ArgumentException("Every descriptor participant requires one independently resolved root handle.", nameof(requests));
        var byRoot = requests.ToDictionary(static request => request.RootIdentity);
        var ordered = descriptor.Participants.Select(participant =>
        {
            if (!byRoot.TryGetValue(participant.RootIdentity, out var request) || request.RootHandle is null ||
                request.EnrollmentEpoch != participant.EnrollmentEpoch)
                throw new ArgumentException("Resolved roots do not match the immutable participant set.", nameof(requests));
            return request;
        }).ToArray();

        var participants = new List<NativeGroupParticipant>(ordered.Length);
        PhysicalStoreLock.PhysicalStoreLockUnionOwner? union = null;
        var transferred = false;
        Exception? failure = null;
        try
        {
            foreach (var request in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireRoot(request.RootHandle, request.RootIdentity);
                var control = OpenControl(request.RootHandle);
                PhysicalStoreLock.RootLockScope? rootScope = null;
                try
                {
                    var initial = ReadLedger(request.RootHandle, control, out var identity);
                    RequireGroupLedgerForDescriptor(initial, descriptor.GetParticipant(request.RootIdentity), descriptor);
                    rootScope = await _locks.AcquireRootLockAsync(control, request.RootIdentity, cancellationToken).ConfigureAwait(false);
                    control = null!;
                    participants.Add(new NativeGroupParticipant(request, descriptor.GetParticipant(request.RootIdentity),
                        rootScope, rootScope.ControlDirectory, initial, identity));
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

            foreach (var participant in participants)
            {
                participant.RootScope.VerifyCanonical();
                var locked = ReadLedger(participant.Request.RootHandle, participant.ControlDirectory, out var identity);
                RequireGroupLedgerForDescriptor(locked, participant.Local, descriptor);
                RequireSameDigest(participant.InitialLedger, locked);
                if (participant.InitialLedgerIdentity != identity)
                    throw Refused("A group membership ledger identity changed during root-lock acquisition.");
                participant.LockedLedger = locked;
                participant.LockedLedgerIdentity = identity;
            }

            union = await _locks.AcquireMemberLockUnionAsync(
                participants.Select(static item => item.RootScope).ToArray(),
                participants.Select(static item => new PhysicalStoreLock.RootMemberLockRequest(item.RootScope,
                    item.LockedLedger!.Members.Select(GetSlot).ToArray())).ToArray(), cancellationToken).ConfigureAwait(false);

            foreach (var participant in participants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = ReadLedger(participant.Request.RootHandle, participant.ControlDirectory, out var identity);
                RequireGroupLedgerForDescriptor(current, participant.Local, descriptor);
                RequireSameDigest(participant.LockedLedger!, current);
                if (participant.LockedLedgerIdentity != identity)
                    throw Refused("A group membership ledger changed after the complete root/member lock union was acquired.");
                participant.LockedLedger = current;
                participant.LockedLedgerIdentity = identity;
                participant.Scope = new MemberLocatorReplayScope(current);
                participant.Locations = ResolveMemberLocatorMap(current, participant.Scope,
                    LocatorReplayBindingPolicy.GroupAcknowledged);
                var selected = participant.Locations[participant.Local.PriorMember.MemberId];
                if (selected.Slot != descriptor.SharedStateSlot)
                    throw Refused("The configured member locators do not converge on the exact shared state slot.");
                selected.Revalidate();
                participant.SelectedLocation = selected;
            }

            foreach (var participant in participants)
            {
                var share = union.CreateShare();
                participant.PendingShare = share;
                participant.Transaction = new Transaction(this, participant.Request.RootHandle,
                    participant.ControlDirectory, share, participant.LockedLedger!, participant.LockedLedgerIdentity!,
                    ownsControlDirectory: false);
                participant.PendingShare = null;
            }
            await union.DisposeAsync().ConfigureAwait(false);
            union = null;
            var owner = new NativeGroupPublicationOwner(this, descriptor, participants);
            transferred = true;
            return owner;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            if (!transferred)
            {
                var errors = new List<Exception>();
                for (var index = participants.Count - 1; index >= 0; index--)
                {
                    var item = participants[index];
                    try { if (item.Locations is not null) DisposeLocations(item.Locations.Values); }
                    catch (Exception exception) { errors.Add(exception); }
                    try { item.Scope?.Expire(); } catch (Exception exception) { errors.Add(exception); }
                    try
                    {
                        if (item.Transaction is not null) await item.Transaction.DisposeAsync().ConfigureAwait(false);
                        else if (item.PendingShare is not null) await item.PendingShare.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception) { errors.Add(exception); }
                }
                if (union is not null)
                {
                    try { await union.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { errors.Add(exception); }
                }
                else if (participants.Count > 0)
                {
                    for (var index = participants.Count - 1; index >= 0; index--)
                    {
                        try { await participants[index].RootScope.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception exception) { errors.Add(exception); }
                    }
                }
                if (errors.Count > 0)
                    throw new AggregateException("Native group acquisition failed and retained resources did not all release.",
                        (failure is null ? Enumerable.Empty<Exception>() : [failure]).Concat(errors));
            }
        }
    }

    private static void RequireGroupLedgerForDescriptor(RootMembershipRecord ledger,
        GroupPublicationParticipantV2 local, GroupPublicationDescriptorV2 descriptor)
    {
        if (ledger.RootIdentity != local.RootIdentity || ledger.EnrollmentEpoch != local.EnrollmentEpoch ||
            ledger.PendingStateCommit is not null || ledger.Members.Count == 0 ||
            !ledger.Members.Select(static member => member.MemberId).ToHashSet(StringComparer.Ordinal)
                .SetEquals(ledger.TargetMemberIds) ||
            ledger.Members.Any(static member => member.Binding is not (RootMemberRecord.ProspectiveBinding or
                RootMemberRecord.AcknowledgedBinding or RootMemberRecord.BundleAcknowledgedBinding or
                RootMemberRecord.ExistingUnprotectedBinding)))
            throw Refused("A group owner requires the exact bound member union and root authority.");
        if (!string.Equals(ProtectionDigest.Ledger(ledger), ledger.LedgerDigest, StringComparison.Ordinal))
            throw Refused("A group participant ledger digest is invalid.");
        var member = ledger.Members.SingleOrDefault(item => item.MemberId == local.PriorMember.MemberId)
            ?? throw Refused("A selected group member is missing from its root ledger.");
        var isPrior = ledger.Status == local.PriorMembershipStatus && ledger.SchemaVersion == local.PriorSchemaVersion &&
            ledger.PendingGroupPublicationV2 is null && ledger.LedgerDigest == local.PriorLedgerDigest &&
            RootMembershipRecord.BindingsEqualForGroup(member.Binding, local.PriorMember.Binding);
        var pending = ledger.PendingGroupPublicationV2;
        var isPending = ledger.Status == RootMembershipStatus.Incomplete && ledger.SchemaVersion == RootMembershipRecord.BundleSchemaVersion &&
            pending is not null && pending.RootIdentity == local.RootIdentity &&
            pending.Descriptor.IntentDigest == descriptor.IntentDigest &&
            RootMembershipRecord.BindingsEqualForGroup(member.Binding, local.PriorMember.Binding);
        var isNextAck = ledger.Status == RootMembershipStatus.Complete && ledger.SchemaVersion == RootMembershipRecord.BundleSchemaVersion &&
            pending is null && member.Binding is RootMemberRecord.BundleAcknowledgedBinding binding &&
            binding.LogicalMemberId == descriptor.LogicalMemberId && binding.ParticipantSetDigest == descriptor.ParticipantSetDigest &&
            binding.PublicationId == descriptor.TransactionId && binding.StateGeneration == descriptor.NextStateGeneration &&
            binding.StateBodyDigest == descriptor.NextStateBodyDigest && binding.BundleDigest == descriptor.NextBundleDigest &&
            binding.RootRow.RootIdentity == local.RootIdentity && binding.RootRow.EnrollmentEpoch == local.EnrollmentEpoch &&
            binding.RootRow.MemberId == local.PriorMember.MemberId && binding.RootRow.Revision == local.NextRevision &&
            binding.RootRow.StateGeneration == descriptor.NextStateGeneration &&
            binding.RootRow.StateBodyDigest == descriptor.NextStateBodyDigest &&
            binding.RootRow.ProtectionDigest == local.NextRowDigest &&
            !binding.RootRow.LegacyUnknownRecovery &&
            binding.RootRow.ActiveClosure.Knowledge == PackageProtectionClosureKnowledge.Known &&
            binding.RootRow.RecoverableClosure.Knowledge == PackageProtectionClosureKnowledge.Known &&
            string.Equals(ProtectionDigest.PackageProtectionBundleRow(binding.RootRow), binding.RootRow.ProtectionDigest,
                StringComparison.Ordinal);
        if (!isPrior && !isPending && !isNextAck)
            throw Refused("A group participant is neither the exact prior, intent-pending, nor acknowledged Next state.");
        if (!isPrior && ReconstructPriorLedgerFromCandidate(ledger, local).LedgerDigest != local.PriorLedgerDigest)
            throw Refused("A candidate group ledger changed unrelated members, targets, or retirement evidence from its immutable prior.");
    }

    private static RootMembershipRecord ReconstructPriorLedgerFromCandidate(RootMembershipRecord candidate,
        GroupPublicationParticipantV2 local)
    {
        var priorMembers = candidate.Members.Select(member => member.MemberId == local.PriorMember.MemberId
            ? local.PriorMember
            : member).ToArray();
        var prior = new RootMembershipRecord(local.PriorSchemaVersion, candidate.RootIdentity, candidate.EnrollmentEpoch,
            local.PriorMembershipStatus, priorMembers, candidate.TargetMemberIds, candidate.RetiredMembers, null,
            PlaceholderDigest);
        return new RootMembershipRecord(prior.SchemaVersion, prior.RootIdentity, prior.EnrollmentEpoch, prior.Status,
            prior.Members, prior.TargetMemberIds, prior.RetiredMembers, null, ProtectionDigest.Ledger(prior));
    }

    private static RootMembershipRecord BuildGroupLedger(RootMembershipRecord source,
        RootMembershipStatus status, IEnumerable<RootMemberRecord> members, PendingGroupPublicationV2? pending)
    {
        var candidate = new RootMembershipRecord(RootMembershipRecord.BundleSchemaVersion, source.RootIdentity,
            source.EnrollmentEpoch, status, members, source.TargetMemberIds, source.RetiredMembers, null,
            PlaceholderDigest, pending);
        return new RootMembershipRecord(candidate.SchemaVersion, candidate.RootIdentity, candidate.EnrollmentEpoch,
            candidate.Status, candidate.Members, candidate.TargetMemberIds, candidate.RetiredMembers, null,
            ProtectionDigest.Ledger(candidate), pending);
    }

    private static PackageProtectionBundle RequireNextBundle(GroupPublicationDescriptorV2 descriptor, StoreStateRecord state)
    {
        if (state.ProtectionRecord is not null || state.ProtectionBundle is not { } bundle ||
            bundle.SchemaVersion != PackageProtectionBundle.CurrentSchemaVersion ||
            bundle.LogicalMemberId != descriptor.LogicalMemberId || bundle.PublicationId != descriptor.TransactionId ||
            bundle.StateGeneration != descriptor.NextStateGeneration || bundle.StateBodyDigest != descriptor.NextStateBodyDigest ||
            bundle.ParticipantSetDigest != descriptor.ParticipantSetDigest || bundle.BundleDigest != descriptor.NextBundleDigest ||
            ProtectionDigest.StateBody(state) != bundle.StateBodyDigest ||
            bundle.Rows.Count != descriptor.Participants.Count ||
            !bundle.Rows.Select(static row => row.RootIdentity).ToHashSet().SetEquals(descriptor.Participants.Select(static item => item.RootIdentity)))
            throw Refused("The next payload does not match the immutable multiroot group descriptor.");
        foreach (var participant in descriptor.Participants)
        {
            var row = bundle.Rows.Single(item => item.RootIdentity == participant.RootIdentity);
            if (row.RootIdentity != participant.RootIdentity || row.EnrollmentEpoch != participant.EnrollmentEpoch ||
                row.MemberId != participant.PriorMember.MemberId || row.Revision != participant.NextRevision ||
                row.StateGeneration != descriptor.NextStateGeneration || row.StateBodyDigest != descriptor.NextStateBodyDigest ||
                row.ProtectionDigest != participant.NextRowDigest || row.LegacyUnknownRecovery ||
                row.ActiveClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
                row.RecoverableClosure.Knowledge != PackageProtectionClosureKnowledge.Known)
                throw Refused("A next bundle row does not match its exact root-local participant tuple.");
        }
        return bundle;
    }

    private static void RequireExactGroupBundle(StoreStateRecord state, GroupPublicationDescriptorV2 descriptor,
        StoreStateRecord expected)
    {
        RequireNextBundle(descriptor, state);
        var bundle = state.ProtectionBundle!;
        if (ProtectionDigest.StateBody(state) != ProtectionDigest.StateBody(expected) ||
            !bundle.HasSamePayloadAs(expected.ProtectionBundle!))
            throw Refused("The reopened shared state or bundle differs from the exact staged group payload.");
    }

    private async Task<byte[]> EncodeGroupStateAsync(StoreStateRecord state, CancellationToken cancellationToken)
    {
        using var buffer = new BoundedControlPayloadStream(MaximumStateBytes);
        await _stateSerializer.WritePayloadAsync(buffer, state, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return buffer.ToArray();
    }

    private async Task RequireGroupBundleArtifactAsync(PhysicalStoreDirectoryHandle parent, string name,
        PhysicalFileIdentity identity, GroupPublicationDescriptorV2 descriptor, StoreStateRecord expected,
        CancellationToken cancellationToken)
    {
        var state = await DecodeStateAsync(ReadFile(parent, name, identity, MaximumStateBytes).Bytes, cancellationToken).ConfigureAwait(false);
        RequireExactGroupBundle(state, descriptor, expected);
    }

    private async Task<StateObservation> ReadGroupStateAsync(PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot, PhysicalFileIdentity identity, CancellationToken cancellationToken)
    {
        PhysicalStorePublicationChecks.RequireParent(_files, parent, slot.ParentIdentity);
        var read = ReadFile(parent, slot.CanonicalBasename, identity, MaximumStateBytes);
        var observed = new PhysicalStoreIdentity(_files).ObserveStateSlot(parent, slot.CanonicalBasename);
        if (observed.Slot != slot || observed.FileIdentity != read.Identity)
            throw Refused("The shared group state payload no longer binds its exact native slot.");
        return new StateObservation(read.Identity, read.Bytes,
            await DecodeStateAsync(read.Bytes, cancellationToken).ConfigureAwait(false));
    }

    private void RemoveExactArtifact(PhysicalStoreDirectoryHandle parent, string name, PhysicalFileIdentity expected)
    {
        PhysicalStorePublicationChecks.RequireExpectedEntry(_files.InspectChildNoFollow(parent, name), expected);
        PhysicalStorePublicationChecks.PrepareRemoval(_files, _names, parent, name, expected);
        _publication.RemoveControlFileAt(parent, name, expected);
    }

    private static PackageStoreAdmissionException Refused(string message, PhysicalRootIdentity root)
        => new(PackageStoreAdmissionReason.StateMismatch, message, root);

    private void RemoveExactArtifactIfPresent(PhysicalStoreDirectoryHandle parent, string name, PhysicalFileIdentity expected)
    {
        var actual = _files.InspectChildNoFollow(parent, name);
        if (actual is null)
            return;
        PhysicalStorePublicationChecks.RequireExpectedEntry(actual, expected);
        PhysicalStorePublicationChecks.PrepareRemoval(_files, _names, parent, name, expected);
        _publication.RemoveControlFileAt(parent, name, expected);
    }

    private sealed class NativeGroupParticipant(
        NativeGroupRootRequest request,
        GroupPublicationParticipantV2 local,
        PhysicalStoreLock.RootLockScope rootScope,
        PhysicalStoreDirectoryHandle controlDirectory,
        RootMembershipRecord initialLedger,
        PhysicalFileIdentity initialLedgerIdentity)
    {
        internal NativeGroupRootRequest Request { get; } = request;
        internal GroupPublicationParticipantV2 Local { get; } = local;
        internal PhysicalStoreLock.RootLockScope RootScope { get; } = rootScope;
        internal PhysicalStoreDirectoryHandle ControlDirectory { get; } = controlDirectory;
        internal RootMembershipRecord InitialLedger { get; } = initialLedger;
        internal PhysicalFileIdentity InitialLedgerIdentity { get; } = initialLedgerIdentity;
        internal RootMembershipRecord? LockedLedger { get; set; }
        internal PhysicalFileIdentity? LockedLedgerIdentity { get; set; }
        internal MemberLocatorReplayScope? Scope { get; set; }
        internal IReadOnlyDictionary<string, ResolvedMemberStateLocation>? Locations { get; set; }
        internal ResolvedMemberStateLocation? SelectedLocation { get; set; }
        internal IAsyncDisposable? PendingShare { get; set; }
        internal Transaction? Transaction { get; set; }
    }

    private sealed class NativeGroupPublicationOwner(
        RootMembershipRegistry registry,
        GroupPublicationDescriptorV2 descriptor,
        IReadOnlyList<NativeGroupParticipant> participants) : IAsyncDisposable
    {
        private readonly ResolvedMemberStateLocation _stableSharedLocation = participants[0].SelectedLocation!;
        private readonly MemberLocatorReplayScope _stableSharedScope = participants[0].Scope!;
        private readonly PhysicalStoreDirectoryHandle _stableSharedParent = participants[0].SelectedLocation!.Parent;
        private bool _disposed;
        internal GroupPublicationDescriptorV2 Descriptor { get; } = descriptor;
        internal IReadOnlyList<NativeGroupParticipant> Participants { get; } = participants;
        internal PhysicalStoreDirectoryHandle SharedParent
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                RequireStableSharedParent();
                return _stableSharedParent;
            }
        }

        private void RequireStableSharedParent()
        {
            var observed = registry._files.InspectHandle(_stableSharedParent);
            if (observed.Kind != PhysicalStoreEntryKind.Directory ||
                observed.Identity != Descriptor.SharedStateSlot.ParentIdentity)
                throw Refused("The retained shared-state parent no longer matches the immutable group slot.");
        }

        internal async Task RequireAllPriorAsync(CancellationToken token)
        {
            foreach (var participant in Participants)
            {
                token.ThrowIfCancellationRequested();
                var ledger = participant.Transaction!.ReadCurrent();
                if (ledger.LedgerDigest != participant.Local.PriorLedgerDigest ||
                    ledger.SchemaVersion != participant.Local.PriorSchemaVersion || ledger.Status != participant.Local.PriorMembershipStatus ||
                    ledger.PendingGroupPublicationV2 is not null)
                    throw Refused("New group publication requires every participant at its exact pre-intent ledger.");
            }
            var prior = await ReadAndVerifyPriorAsync(token).ConfigureAwait(false);
            if (Descriptor.Participants[0].PriorStateFileIdentity is null && prior is not null)
                throw Refused("An absent prior shared slot unexpectedly contains payload bytes.");
            if (prior?.State.ProtectionBundle is { } priorBundle)
                VerifyPriorBundleAndInstalls(priorBundle, prior.State, token);
        }

        internal void VerifyNextBundleAndInstalls(PackageProtectionBundle bundle, StoreStateRecord state,
            CancellationToken token) => VerifyBundleAndInstalls(bundle, state, token, prior: false);

        private void VerifyPriorBundleAndInstalls(PackageProtectionBundle bundle, StoreStateRecord state,
            CancellationToken token) => VerifyBundleAndInstalls(bundle, state, token, prior: true);

        private void VerifyBundleAndInstalls(PackageProtectionBundle bundle, StoreStateRecord state,
            CancellationToken token, bool prior)
        {
            var roots = bundle.Rows.Select(static row => row.RootIdentity).ToHashSet();
            var installObservations = new List<PackageInstallIdentityReader.PackageInstallIdentityObservation>();
            var pathObservations = new List<ResolvedPackageStorePath>();
            try
            {
                var resolver = new PackageStoreAuthorityResolver(registry._files, registry);
            var reader = new PackageInstallIdentityReader(registry._files);
                var graphs = PersistedStoreStateBundleGraphVerifier.Verify(state, bundle, roots);
                foreach (var participant in Participants)
                {
                    token.ThrowIfCancellationRequested();
                    var root = participant.Request.RootIdentity;
                    var local = participant.Local;
                    var row = bundle.Rows.SingleOrDefault(item => item.RootIdentity == root)
                        ?? throw Refused("The shared bundle omits a locked participant root.");
                    var expectedRow = prior ? local.PriorRow : row;
                    var expectedRevision = prior ? local.PriorRevision : local.NextRevision;
                    if (prior && expectedRow is null || expectedRow is not null && !expectedRow.HasSamePayloadAs(row) ||
                        row.RootIdentity != root || row.EnrollmentEpoch != participant.Request.EnrollmentEpoch ||
                        row.MemberId != local.PriorMember.MemberId || row.Revision != expectedRevision ||
                        row.StateGeneration != bundle.StateGeneration || row.StateBodyDigest != bundle.StateBodyDigest ||
                        row.ProtectionDigest != (prior ? expectedRow!.ProtectionDigest : local.NextRowDigest))
                        throw Refused("The bundle row differs from the immutable root-local acknowledgement.");
                    var allGraphs = graphs.ActiveGraphs.Concat(graphs.RecoverableGraphs).ToArray();
                    var installs = allGraphs.SelectMany(static graph => graph.Nodes).Select(static node => node.Install)
                        .Where(install => install.Root == root).Distinct().ToArray();
                    foreach (var install in installs)
                    {
                        token.ThrowIfCancellationRequested();
                        var observation = reader.Observe(participant.Request.RootHandle, root,
                            install.RootRelativeInstallPath, install.PackageId, install.Version);
                        if (observation.InstallIdentity.DirectoryIdentity != install.DirectoryIdentity ||
                            !string.Equals(observation.InstallIdentity.CompletionIdentity, install.CompletionIdentity,
                                StringComparison.Ordinal))
                        {
                            observation.Dispose();
                            throw Refused("A root-local v2 graph install no longer matches its persisted native identity.", root);
                        }
                        installObservations.Add(observation);
                    }

                    foreach (var descriptor in state.ActivePackageDescriptorsByIdNormalized.Values)
                    {
                        var associated = graphs.ActiveGraphs.SelectMany(static graph => graph.Nodes)
                            .Select(static node => node.Install)
                            .Where(install => install.Root == root &&
                                string.Equals(install.PackageId, descriptor.PackageId, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(install.Version, descriptor.Version, StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                        if (associated.Length == 0)
                            continue;
                        var resolved = resolver.ResolveProtectedInstallPath(descriptor.InstallPath, participant.Scope!);
                        pathObservations.Add(resolved);
                        var target = registry._files.InspectHandle(resolved.Target);
                        if (target.Kind != PhysicalStoreEntryKind.Directory ||
                            associated.Any(install => install.DirectoryIdentity != target.Identity))
                            throw Refused("An active descriptor path does not identify this root's persisted v2 install.", root);
                    }
                }
                foreach (var observation in installObservations)
                    observation.Revalidate();
                foreach (var observation in pathObservations)
                    observation.Revalidate();
            }
            finally
            {
                for (var index = pathObservations.Count - 1; index >= 0; index--)
                    pathObservations[index].Dispose();
                for (var index = installObservations.Count - 1; index >= 0; index--)
                    installObservations[index].Dispose();
            }
        }

        internal async Task EnsureIntentAcrossGroupAsync(CancellationToken token,
            Action<NativeGroupPublicationPoint, PhysicalRootIdentity?>? checkpoint)
        {
            foreach (var participant in Participants)
            {
                token.ThrowIfCancellationRequested();
                var ledger = participant.Transaction!.ReadCurrent();
                if (ledger.PendingGroupPublicationV2 is { } existing)
                {
                    if (existing.Descriptor.IntentDigest != Descriptor.IntentDigest)
                        throw Refused("A participant retains a conflicting group intent.");
                    continue;
                }
                var selected = ledger.Members.SingleOrDefault(item => item.MemberId == participant.Local.PriorMember.MemberId);
                if (ledger.Status == RootMembershipStatus.Complete &&
                    ledger.SchemaVersion == RootMembershipRecord.BundleSchemaVersion &&
                    selected?.Binding is RootMemberRecord.BundleAcknowledgedBinding acknowledged &&
                    acknowledged.LogicalMemberId == Descriptor.LogicalMemberId &&
                    acknowledged.ParticipantSetDigest == Descriptor.ParticipantSetDigest &&
                    acknowledged.PublicationId == Descriptor.TransactionId &&
                    acknowledged.StateGeneration == Descriptor.NextStateGeneration &&
                    acknowledged.StateBodyDigest == Descriptor.NextStateBodyDigest &&
                    acknowledged.BundleDigest == Descriptor.NextBundleDigest &&
                    acknowledged.RootRow.Revision == participant.Local.NextRevision &&
                    acknowledged.RootRow.ProtectionDigest == participant.Local.NextRowDigest)
                    continue;
                if (ledger.Status != participant.Local.PriorMembershipStatus ||
                    ledger.SchemaVersion != participant.Local.PriorSchemaVersion ||
                    ledger.LedgerDigest != participant.Local.PriorLedgerDigest)
                    throw Refused("An untouched participant is not at its exact immutable pre-intent ledger.");
                var pending = new PendingGroupPublicationV2(Descriptor, participant.Request.RootIdentity,
                    GroupPublicationPhaseV2.Intent, null, null, GroupPublicationResolutionV2.Unresolved);
                participant.Transaction.Publish(BuildGroupLedger(ledger, RootMembershipStatus.Incomplete,
                    ledger.Members, pending));
                checkpoint?.Invoke(NativeGroupPublicationPoint.IntentPublished, participant.Request.RootIdentity);
                RefreshParticipantMemberLocatorScope(participant);
            }
        }

        internal async Task<StateObservation?> ReadAndVerifyPriorAsync(CancellationToken token)
        {
            var local = Descriptor.Participants[0];
            if (local.PriorStateFileIdentity is null)
            {
                if (registry._files.InspectChildNoFollow(SharedParent, Descriptor.SharedStateSlot.CanonicalBasename) is not null)
                    throw Refused("The group prior slot must be absent in every participant.");
                return null;
            }

            StateObservation? common = null;
            foreach (var participant in Participants)
            {
                token.ThrowIfCancellationRequested();
                var member = participant.Transaction!.ReadCurrent().Members.Single(item => item.MemberId == participant.Local.PriorMember.MemberId);
                var binding = member.Binding;
                var identity = binding switch
                {
                    RootMemberRecord.ExistingUnprotectedBinding unprotected => unprotected.ObservedStateFileIdentity,
                    RootMemberRecord.AcknowledgedBinding acknowledged => acknowledged.ObservedStateFileIdentity,
                    RootMemberRecord.BundleAcknowledgedBinding bundle => bundle.ObservedStateFileIdentity,
                    _ => null
                };
                if (identity != local.PriorStateFileIdentity)
                    throw Refused("A participant prior member does not bind the common shared payload identity.");
                var state = await registry.ReadGroupStateAsync(SharedParent,
                    Descriptor.SharedStateSlot, identity!, token).ConfigureAwait(false);
                RequirePriorStateForParticipant(state.State, participant.Local, Descriptor);
                if (common is not null && (common.Identity != state.Identity ||
                    ProtectionDigest.StateBody(common.State) != ProtectionDigest.StateBody(state.State)))
                    throw Refused("Participants do not observe one exact shared prior payload.");
                common ??= state;
            }
            return common;
        }

        internal async Task RequirePriorArtifactAsync(string name, PhysicalFileIdentity identity, CancellationToken token)
        {
            var artifactSlot = new StateSlotIdentity(Descriptor.SharedStateSlot.ParentIdentity,
                Descriptor.SharedStateSlot.NameSemantics, name);
            var artifact = await registry.ReadGroupStateAsync(SharedParent, artifactSlot,
                identity, token).ConfigureAwait(false);
            foreach (var participant in Participants)
                RequirePriorStateForParticipant(artifact.State, participant.Local, Descriptor);
        }

        internal async Task<StateObservation?> TryReadCurrentGroupStateAsync(CancellationToken token)
        {
            var identity = registry._files.InspectChildNoFollow(SharedParent,
                Descriptor.SharedStateSlot.CanonicalBasename)?.Identity;
            if (identity is null)
                return null;
            return await registry.ReadGroupStateAsync(SharedParent, Descriptor.SharedStateSlot, identity, token).ConfigureAwait(false);
        }

        internal async Task<StateObservation> ReadCurrentGroupStateAsync(CancellationToken token)
            => await TryReadCurrentGroupStateAsync(token).ConfigureAwait(false)
               ?? throw Refused("The shared group state slot is absent.");

        internal async Task<bool> IsExactPriorStateAsync(StateObservation? current, CancellationToken token)
        {
            var expectedIdentity = Descriptor.Participants[0].PriorStateFileIdentity;
            if (expectedIdentity is null)
            {
                if (current is not null)
                    return false;
            }
            else if (current is null || current.Identity != expectedIdentity)
            {
                return false;
            }
            var prior = await ReadAndVerifyPriorAsync(token).ConfigureAwait(false);
            if (prior is null)
                return current is null;
            if (current is null || prior.Identity != current.Identity ||
                ProtectionDigest.StateBody(prior.State) != ProtectionDigest.StateBody(current.State))
                return false;
            if (prior.State.ProtectionBundle is { } bundle)
                VerifyPriorBundleAndInstalls(bundle, prior.State, token);
            return true;
        }

        internal async Task RequireBoundArtifactsConsistentAsync(CancellationToken token, bool allowMissingBackup)
        {
            var ledgers = Participants.Select(item => item.Transaction!.ReadCurrent()).ToArray();
            var pending = ledgers.Select(static ledger => ledger.PendingGroupPublicationV2)
                .Where(static item => item is not null).Cast<PendingGroupPublicationV2>().ToArray();
            if (pending.Length == 0)
                throw Refused("No participant retains the bound group artifact tuple.");
            RequireConsistentBoundTuple(pending);
            if (ledgers.Any(ledger => ledger.PendingGroupPublicationV2 is null &&
                    (ledger.Status != RootMembershipStatus.Complete || ledger.SchemaVersion != RootMembershipRecord.BundleSchemaVersion)))
                throw Refused("A participant without a pending group record is not an acknowledged Next ledger.");
            var first = pending[0];
            var stageName = Descriptor.Participants[0].StagedName;
            var stage = registry._files.InspectChildNoFollow(SharedParent, stageName);
            var shared = registry._files.InspectChildNoFollow(SharedParent, Descriptor.SharedStateSlot.CanonicalBasename);
            if (stage is not null && stage.Identity != first.StagedStateFileIdentity)
                throw Refused("A bound stage artifact changed identity.");
            if (shared?.Identity != first.StagedStateFileIdentity)
                throw Refused("The shared payload is neither the exact bound Next state nor prior state.");
            if (first.BackupStateFileIdentity is { } backupIdentity)
            {
                var backup = registry._files.InspectChildNoFollow(SharedParent, Descriptor.Participants[0].BackupName!);
                var allNextResolved = ledgers.All(static ledger => ledger.PendingGroupPublicationV2 is null ||
                    ledger.PendingGroupPublicationV2.Resolution == GroupPublicationResolutionV2.Next);
                if (backup is null && allowMissingBackup && allNextResolved)
                    return;
                if (backup is null || backup.Identity != backupIdentity)
                    throw Refused("A bound prior backup is missing or changed.");
                await RequirePriorArtifactAsync(Descriptor.Participants[0].BackupName!, backupIdentity, token).ConfigureAwait(false);
            }
        }

        internal void RequireConsistentBoundTuple(IReadOnlyList<PendingGroupPublicationV2> pending)
        {
            if (pending.Count == 0 || pending.Select(static item => item.BoundCommitDigest).Distinct().Count() != 1 ||
                pending.Select(static item => item.StagedStateFileIdentity).Distinct().Count() != 1 ||
                pending.Select(static item => item.BackupStateFileIdentity).Distinct().Count() != 1 ||
                pending.Any(static item => item.Phase is not (GroupPublicationPhaseV2.ArtifactsBound or GroupPublicationPhaseV2.Resolved)))
                throw Refused("Bound participant records disagree about their immutable artifact identities.");
        }

        internal void RequirePlannedArtifactsAbsent()
        {
            var local = Descriptor.Participants[0];
            if (registry._files.InspectChildNoFollow(SharedParent, local.StagedName) is not null ||
                local.BackupName is not null && registry._files.InspectChildNoFollow(SharedParent, local.BackupName) is not null)
                throw Refused("Unbound or unknown group artifacts remain untouched.");
        }

        internal void RefreshMemberLocatorScopes()
        {
            RequireStableSharedParent();
            var ledgers = Participants.Select(static participant => participant.Transaction!.ReadCurrent()).ToArray();
            for (var index = 0; index < Participants.Count; index++)
                RequireGroupLedgerForDescriptor(ledgers[index], Participants[index].Local, Descriptor);
            var pendingRows = ledgers.Select(static ledger => ledger.PendingGroupPublicationV2).ToArray();
            if (pendingRows.Any(static pending => pending is null))
                throw Refused("Locator scopes can refresh only at a complete participant group barrier.");
            var pending = pendingRows.Cast<PendingGroupPublicationV2>().ToArray();
            if (pending.Select(static item => item.Descriptor.IntentDigest).Distinct(StringComparer.Ordinal).Count() != 1 ||
                pending.Select(static item => item.Phase).Distinct().Count() != 1 ||
                pending.Select(static item => item.Resolution).Distinct().Count() != 1 ||
                pending[0].Phase is not (GroupPublicationPhaseV2.Intent or GroupPublicationPhaseV2.ArtifactsBound or
                    GroupPublicationPhaseV2.Resolved) ||
                pending[0].Phase != GroupPublicationPhaseV2.Resolved &&
                    pending[0].Resolution != GroupPublicationResolutionV2.Unresolved ||
                pending[0].Phase == GroupPublicationPhaseV2.Resolved &&
                    pending[0].Resolution == GroupPublicationResolutionV2.Unresolved)
                throw Refused("Locator scopes can refresh only after one coherent durable group transition.");
            if (pending.Any(static item => item.StagedStateFileIdentity is not null))
                RequireConsistentBoundTuple(pending);
            else if (pending.Any(static item => item.BackupStateFileIdentity is not null) ||
                     pending[0].Resolution == GroupPublicationResolutionV2.Next)
                throw Refused("A group decision cannot omit its exact bound artifact identity.");

            _stableSharedScope.Expire();
            foreach (var participant in Participants)
                RefreshParticipantMemberLocatorScope(participant);
            RequireStableSharedParent();
        }

        private void RefreshParticipantMemberLocatorScope(NativeGroupParticipant participant)
        {
            var previousScope = participant.Scope;
            var previousLocations = participant.Locations;
            participant.Locations = null;
            participant.SelectedLocation = null;
            try
            {
                if (previousLocations is not null)
                    DisposeLocationsSafely(previousLocations.Values.Where(location =>
                        !ReferenceEquals(location, _stableSharedLocation)));
            }
            finally
            {
                previousScope?.Expire();
            }

            var current = participant.Transaction!.ReadCurrent();
            RequireGroupLedgerForDescriptor(current, participant.Local, Descriptor);
            var scope = new MemberLocatorReplayScope(current);
            IReadOnlyDictionary<string, ResolvedMemberStateLocation>? locations = null;
            try
            {
                locations = registry.ResolveMemberLocatorMap(current, scope,
                    LocatorReplayBindingPolicy.GroupAcknowledged);
                var selected = locations[participant.Local.PriorMember.MemberId];
                var parent = registry._files.InspectHandle(selected.Parent);
                if (selected.Slot != Descriptor.SharedStateSlot ||
                    parent.Kind != PhysicalStoreEntryKind.Directory ||
                    parent.Identity != Descriptor.SharedStateSlot.ParentIdentity)
                    throw Refused("The refreshed participant locator no longer identifies the exact group slot.");
                selected.Revalidate();
                participant.Scope = scope;
                participant.Locations = locations;
                participant.SelectedLocation = selected;
                locations = null;
            }
            catch (Exception failure)
            {
                var cleanupErrors = new List<Exception>();
                if (locations is not null)
                {
                    try { DisposeLocationsSafely(locations.Values); }
                    catch (Exception exception) { cleanupErrors.Add(exception); }
                }
                try { scope.Expire(); }
                catch (Exception exception) { cleanupErrors.Add(exception); }
                if (cleanupErrors.Count > 0)
                    throw new AggregateException("A locator-scope refresh failed and its replacement resources did not all release.",
                        new[] { failure }.Concat(cleanupErrors));
                throw;
            }
        }

        private static void DisposeLocationsSafely(IEnumerable<ResolvedMemberStateLocation> locations)
        {
            var errors = new List<Exception>();
            foreach (var location in locations.Reverse())
            {
                try { location.Dispose(); }
                catch (Exception exception) { errors.Add(exception); }
            }
            if (errors.Count == 1)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
            if (errors.Count > 1)
                throw new AggregateException("Locator-scope locations did not all release.", errors);
        }

        internal async Task RequireBoundArtifactLocationsAsync(PendingGroupPublicationV2 pending, CancellationToken token)
        {
            var local = pending.LocalParticipant;
            var shared = registry._files.InspectChildNoFollow(SharedParent, Descriptor.SharedStateSlot.CanonicalBasename);
            var stage = registry._files.InspectChildNoFollow(SharedParent, local.StagedName);
            if (shared?.Identity == pending.StagedStateFileIdentity)
            {
                if (stage is not null)
                    throw Refused("Both the shared target and planned stage are present for one bound identity.");
            }
            else
            {
                if (stage is null || stage.Identity != pending.StagedStateFileIdentity)
                    throw Refused("The bound Next state is neither staged nor installed under its exact identity.");
                var staged = await registry.ReadGroupStateAsync(SharedParent,
                    new StateSlotIdentity(Descriptor.SharedStateSlot.ParentIdentity,
                        Descriptor.SharedStateSlot.NameSemantics, local.StagedName), stage.Identity, token).ConfigureAwait(false);
                RequireNextBundle(Descriptor, staged.State);
            }

            if (local.BackupName is not null && pending.BackupStateFileIdentity is { } backupIdentity)
                await RequirePriorArtifactAsync(local.BackupName, backupIdentity, token).ConfigureAwait(false);
            else if (pending.BackupStateFileIdentity is not null || local.BackupName is not null)
                throw Refused("The bound backup presence does not match the exact prior slot presence.");
        }

        internal async Task BindIntentRecordsToObservedArtifactsAsync(PendingGroupPublicationV2 bound, CancellationToken token,
            Action<NativeGroupPublicationPoint, PhysicalRootIdentity?>? checkpoint)
        {
            await RequireBoundArtifactLocationsAsync(bound, token).ConfigureAwait(false);
            BindIntentRecordsToBoundTuple(bound, token, checkpoint);
        }

        internal async Task BindIntentRecordsToPriorDecisionAsync(PendingGroupPublicationV2 bound, CancellationToken token,
            Action<NativeGroupPublicationPoint, PhysicalRootIdentity?>? checkpoint)
        {
            await RequireBoundPriorArtifactsConsistentAsync(bound, token).ConfigureAwait(false);
            BindIntentRecordsToBoundTuple(bound, token, checkpoint);
        }

        private void BindIntentRecordsToBoundTuple(PendingGroupPublicationV2 bound, CancellationToken token,
            Action<NativeGroupPublicationPoint, PhysicalRootIdentity?>? checkpoint)
        {
            foreach (var participant in Participants)
            {
                token.ThrowIfCancellationRequested();
                var current = participant.Transaction!.ReadCurrent();
                var pending = current.PendingGroupPublicationV2
                    ?? throw Refused("A participant lost the exact group Intent before binding recovery.");
                if (pending.Descriptor.IntentDigest != Descriptor.IntentDigest)
                    throw Refused("A participant Intent conflicts with the immutable group descriptor.");
                if (pending.Phase == GroupPublicationPhaseV2.Intent)
                    participant.Transaction.Publish(BuildGroupLedger(current, RootMembershipStatus.Incomplete,
                        current.Members, pending.BindArtifacts(bound.StagedStateFileIdentity!, bound.BackupStateFileIdentity)));
                checkpoint?.Invoke(NativeGroupPublicationPoint.ArtifactsBound, participant.Request.RootIdentity);
            }
        }

        private async Task RequireBoundPriorArtifactsConsistentAsync(PendingGroupPublicationV2 pending,
            CancellationToken token)
        {
            if (pending.Resolution is not (GroupPublicationResolutionV2.Prior or GroupPublicationResolutionV2.Unresolved) ||
                pending.StagedStateFileIdentity is null)
                throw Refused("A durable Prior prefix requires one exact bound artifact tuple.");

            var local = pending.LocalParticipant;
            var stage = registry._files.InspectChildNoFollow(SharedParent, local.StagedName);
            if (stage is not null)
            {
                if (stage.Identity != pending.StagedStateFileIdentity)
                    throw Refused("An unrecognized stage artifact remains untouched during Prior recovery.");
                var staged = await registry.ReadGroupStateAsync(SharedParent,
                    new StateSlotIdentity(Descriptor.SharedStateSlot.ParentIdentity,
                        Descriptor.SharedStateSlot.NameSemantics, local.StagedName), stage.Identity, token).ConfigureAwait(false);
                RequireNextBundle(Descriptor, staged.State);
            }

            if (local.BackupName is not null && pending.BackupStateFileIdentity is { } backupIdentity)
            {
                var backup = registry._files.InspectChildNoFollow(SharedParent, local.BackupName);
                if (backup is not null)
                {
                    if (backup.Identity != backupIdentity)
                        throw Refused("An unrecognized backup artifact remains untouched during Prior recovery.");
                    await RequirePriorArtifactAsync(local.BackupName, backup.Identity, token).ConfigureAwait(false);
                }
            }
            else if (pending.BackupStateFileIdentity is not null || local.BackupName is not null)
                throw Refused("The bound backup presence does not match the exact prior slot presence.");
        }

        internal async Task<IReadOnlyList<RootMembershipRecord>> ValidateAcknowledgedNextAsync(CancellationToken token)
        {
            var state = await ReadCurrentGroupStateAsync(token).ConfigureAwait(false);
            var bundle = RequireNextBundle(Descriptor, state.State);
            VerifyNextBundleAndInstalls(bundle, state.State, token);
            foreach (var participant in Participants)
            {
                var ledger = participant.Transaction!.ReadCurrent();
                var member = ledger.Members.Single(item => item.MemberId == participant.Local.PriorMember.MemberId);
                if (member.Binding is not RootMemberRecord.BundleAcknowledgedBinding binding ||
                    binding.ObservedStateFileIdentity != state.Identity || binding.RootRow.Revision != participant.Local.NextRevision ||
                    binding.RootRow.StateGeneration != Descriptor.NextStateGeneration ||
                    binding.RootRow.StateBodyDigest != Descriptor.NextStateBodyDigest ||
                    binding.RootRow.ProtectionDigest != participant.Local.NextRowDigest ||
                    !binding.RootRow.HasSamePayloadAs(bundle.Rows.Single(row => row.RootIdentity == participant.Request.RootIdentity)))
                    throw Refused("A Complete group ledger no longer matches its shared bundle and local row.");
            }
            return Participants.Select(static participant => participant.Transaction!.Ledger).ToArray();
        }

        internal async Task RemoveBoundArtifactsAfterPriorDecisionAsync(PendingGroupPublicationV2 pending,
            CancellationToken token, Action<NativeGroupPublicationPoint, PhysicalRootIdentity?>? checkpoint)
        {
            var parent = SharedParent;
            var participant = pending.LocalParticipant;
            var current = registry._files.InspectChildNoFollow(parent, Descriptor.SharedStateSlot.CanonicalBasename);
            if (participant.PriorStateFileIdentity is null ? current is not null : current?.Identity != participant.PriorStateFileIdentity)
                throw Refused("Prior cleanup requires the exact prior state to remain installed.");
            await RequireBoundPriorArtifactsConsistentAsync(pending, token).ConfigureAwait(false);
            var stage = registry._files.InspectChildNoFollow(parent, participant.StagedName);
            if (stage is not null)
            {
                if (stage.Identity != pending.StagedStateFileIdentity)
                    throw Refused("An unrecognized stage artifact remains untouched.");
                var bytes = registry.ReadFile(parent, participant.StagedName, stage.Identity, MaximumStateBytes).Bytes;
                var state = await registry.DecodeStateAsync(bytes, token).ConfigureAwait(false);
                RequireNextBundle(Descriptor, state);
                registry.RemoveExactArtifact(parent, participant.StagedName, stage.Identity);
                checkpoint?.Invoke(NativeGroupPublicationPoint.PriorStageRemoved, null);
            }
            if (participant.BackupName is not null)
            {
                var backup = registry._files.InspectChildNoFollow(parent, participant.BackupName);
                if (backup is not null)
                {
                    if (backup.Identity != pending.BackupStateFileIdentity)
                        throw Refused("An unrecognized backup artifact remains untouched.");
                    await RequirePriorArtifactAsync(participant.BackupName, backup.Identity, token).ConfigureAwait(false);
                    registry.RemoveExactArtifact(parent, participant.BackupName, backup.Identity);
                    checkpoint?.Invoke(NativeGroupPublicationPoint.PriorBackupRemoved, null);
                }
            }
            if (registry._files.InspectChildNoFollow(parent, participant.StagedName) is not null ||
                participant.BackupName is not null && registry._files.InspectChildNoFollow(parent, participant.BackupName) is not null)
                throw Refused("Prior recovery requires positive absence of every planned artifact.");
        }

        internal RootMembershipRecord ReconstructPriorLedger(RootMembershipRecord current,
            GroupPublicationParticipantV2 local)
        {
            var restored = ReconstructPriorLedgerFromCandidate(current, local);
            if (restored.LedgerDigest != local.PriorLedgerDigest)
                throw Refused("The immutable prior schema and retained rows do not reconstruct the exact pre-intent ledger.");
            return restored;
        }

        internal async Task<IReadOnlyList<RootMembershipRecord>> AcknowledgeNextAsync(PackageProtectionBundle bundle,
            PhysicalFileIdentity installedIdentity,
            Action<NativeGroupPublicationPoint, PhysicalRootIdentity?>? checkpoint,
            CancellationToken token)
        {
            var installedState = await registry.ReadGroupStateAsync(SharedParent, Descriptor.SharedStateSlot,
                installedIdentity, token).ConfigureAwait(false);
            RequireNextBundle(Descriptor, installedState.State);
            VerifyNextBundleAndInstalls(bundle, installedState.State, token);
            foreach (var participant in Participants)
            {
                token.ThrowIfCancellationRequested();
                var current = participant.Transaction!.ReadCurrent();
                if (current.Status == RootMembershipStatus.Complete && current.PendingGroupPublicationV2 is null)
                    continue;
                var pending = current.PendingGroupPublicationV2
                    ?? throw Refused("A local group decision disappeared before Complete acknowledgement.");
                if (pending.Resolution != GroupPublicationResolutionV2.Next || pending.Phase != GroupPublicationPhaseV2.Resolved)
                    throw Refused("Every participant must have the same durable Next resolution before acknowledgement.");
                var selectedId = pending.LocalParticipant.PriorMember.MemberId;
                var old = current.Members.Single(item => item.MemberId == selectedId);
                var row = bundle.Rows.Single(item => item.RootIdentity == participant.Request.RootIdentity);
                var binding = new RootMemberRecord.BundleAcknowledgedBinding(Descriptor.SharedStateSlot, installedIdentity,
                    Descriptor.LogicalMemberId, Descriptor.ParticipantSetDigest, bundle.PublicationId,
                    bundle.StateGeneration, bundle.StateBodyDigest, bundle.BundleDigest, row);
                var members = current.Members.Select(item => item.MemberId == old.MemberId
                    ? new RootMemberRecord(item.MemberId, item.ConfiguredLocator, binding)
                    : item).ToArray();
                participant.Transaction!.Publish(BuildGroupLedger(current, RootMembershipStatus.Complete, members, null));
                checkpoint?.Invoke(NativeGroupPublicationPoint.Acknowledged, participant.Request.RootIdentity);
            }
            return Participants.Select(static participant => participant.Transaction!.Ledger).ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            var errors = new List<Exception>();
            for (var index = Participants.Count - 1; index >= 0; index--)
            {
                var participant = Participants[index];
                if (participant.Locations is not null)
                {
                    try
                    {
                        DisposeLocations(participant.Locations.Values.Where(location =>
                            !ReferenceEquals(location, _stableSharedLocation)));
                    }
                    catch (Exception exception) { errors.Add(exception); }
                }
                if (!ReferenceEquals(participant.Scope, _stableSharedScope))
                    participant.Scope?.Expire();
                try { await participant.Transaction!.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { errors.Add(exception); }
            }
            try { _stableSharedLocation.Dispose(); }
            catch (Exception exception) { errors.Add(exception); }
            _stableSharedScope.Expire();
            if (errors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
            if (errors.Count > 1) throw new AggregateException("Native group owner cleanup failed.", errors);
        }
    }

    private static void RequirePriorStateForParticipant(StoreStateRecord state,
        GroupPublicationParticipantV2 participant, GroupPublicationDescriptorV2 descriptor)
    {
        if (state.ProtectionBundle is not null && state.ProtectionRecord is not null)
            throw Refused("A prior payload cannot contain both v1 and v2 protection metadata.");
        if (participant.PriorMember.Binding is RootMemberRecord.ExistingUnprotectedBinding unprotected)
        {
            if (state.ProtectionRecord is not null || state.ProtectionBundle is not null ||
                ProtectionDigest.StateBody(state) != unprotected.StateBodyDigest)
                throw Refused("The exact unprotected prior payload changed.");
            return;
        }
        if (participant.PriorMember.Binding is RootMemberRecord.AcknowledgedBinding acknowledged)
        {
            if (state.ProtectionBundle is not null || state.ProtectionRecord is null ||
                !state.ProtectionRecord.HasSamePayloadAs(acknowledged.ProtectionRecord) ||
                ProtectionDigest.StateBody(state) != acknowledged.ProtectionRecord.StateBodyDigest)
                throw Refused("The exact v1 prior payload does not match its local acknowledgement.");
            if (participant.PriorRow is null && descriptor.PriorStateGeneration == 0 &&
                ProtectionDigest.StateBody(state) != descriptor.PriorStateBodyDigest)
                throw Refused("The initial v1 prior body digest differs from the immutable descriptor.");
            return;
        }
        if (participant.PriorMember.Binding is RootMemberRecord.BundleAcknowledgedBinding bundleBinding)
        {
            var bundle = state.ProtectionBundle;
            if (state.ProtectionRecord is not null || bundle is null ||
                bundle.LogicalMemberId != descriptor.LogicalMemberId ||
                bundle.ParticipantSetDigest != descriptor.ParticipantSetDigest ||
                bundle.ParticipantSetDigest != bundleBinding.ParticipantSetDigest ||
                bundle.PublicationId != bundleBinding.PublicationId ||
                bundle.StateGeneration != descriptor.PriorStateGeneration ||
                bundle.StateBodyDigest != descriptor.PriorStateBodyDigest ||
                bundle.BundleDigest != descriptor.PriorBundleDigest ||
                ProtectionDigest.StateBody(state) != bundle.StateBodyDigest)
                throw Refused("The existing v2 prior bundle does not match every immutable common header field.");
            var row = bundle.Rows.SingleOrDefault(item => item.RootIdentity == participant.RootIdentity);
            if (row is null || !row.HasSamePayloadAs(bundleBinding.RootRow) ||
                row.Revision != participant.PriorRevision || row.StateGeneration != descriptor.PriorStateGeneration ||
                row.StateBodyDigest != descriptor.PriorStateBodyDigest || row.ProtectionDigest != bundleBinding.RootRow.ProtectionDigest)
                throw Refused("The root-local v2 prior row differs from its acknowledged ledger row.");
            return;
        }
        throw Refused("The group descriptor prior member is not a supported bound state.");
    }
}
