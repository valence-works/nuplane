using System.Text;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.Coordination.GraphUseSerialization;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Coordination;

internal sealed partial class PackageGraphUseRecordStore
{
    /// <summary>Recovers only exact graph-use control artifacts while the caller retains a verified Complete root owner.</summary>
    /// <remarks>
    /// The caller must retain the root and every member lock and independently replay the current Complete
    /// membership and member states for this entire call. This method never acquires that owner, reads package
    /// payloads, removes package installs, or turns a diagnostic process ID or prior stale classification into
    /// mutation authority. Every live-sentinel decision is followed by a fresh removal token before mutation.
    /// </remarks>
    internal async Task<GraphUseRecordRecoveryResult> RecoverAsync(
        PhysicalStoreDirectoryHandle heldRoot,
        PhysicalRootIdentity expectedRoot,
        RootMembershipRecord verifiedCompleteMembership,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(heldRoot);
        ArgumentNullException.ThrowIfNull(expectedRoot);
        ArgumentNullException.ThrowIfNull(verifiedCompleteMembership);
        RequireCompleteBoundary(expectedRoot, verifiedCompleteMembership);
        cancellationToken.ThrowIfCancellationRequested();
        var recovery = _files as IPhysicalStoreControlRecoveryFileSystem ??
            throw Refused("The filesystem provider lacks exact native control-file recovery operations.");

        PhysicalStoreDirectoryHandle? control = null;
        try
        {
            var rootSemantics = VerifyRoot(heldRoot, expectedRoot);
            var controlEntry = _files.InspectChildNoFollow(heldRoot, RootMembershipRegistry.ControlDirectoryName);
            if (controlEntry is null || controlEntry.Kind != PhysicalStoreEntryKind.Directory ||
                !SameVolume(controlEntry.Identity, expectedRoot.HandleIdentity))
            {
                throw Refused("The reserved control directory is missing or does not share the held root's native volume.");
            }

            control = _files.OpenDirectoryChildNoFollow(heldRoot, RootMembershipRegistry.ControlDirectoryName);
            var controlInfo = _files.InspectHandle(control);
            if (controlInfo.Kind != PhysicalStoreEntryKind.Directory || controlInfo.Identity != controlEntry.Identity)
                throw Refused("The opened control directory changed from its no-follow observation.");

            var controlSemantics = _names.ObserveDirectoryNameSemantics(control);
            VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics, control, controlInfo.Identity, controlSemantics);

            // Parse and validate the entire bounded use namespace before the first mutation.
            var initialNames = EnumerateUseArtifactNames(control);
            var parsedPlans = ParseUseArtifactNames(initialNames, allowRecoveryArtifacts: true);
            var expectedFiles = CaptureRecoveryNamespace(control, controlInfo.Identity, controlSemantics,
                expectedRoot, parsedPlans);
            var plans = new List<RecoveryPlan>(parsedPlans.Count);
            foreach (var parsedPlan in parsedPlans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                plans.Add(InitializeRecoveryPlan(parsedPlan, expectedFiles, heldRoot, expectedRoot,
                    verifiedCompleteMembership.EnrollmentEpoch));
            }

            // Replay every parsed record and install closure once more as one complete pre-mutation pass.
            foreach (var plan in plans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReplayPlan(plan, expectedFiles, heldRoot, expectedRoot,
                    verifiedCompleteMembership.EnrollmentEpoch, control, controlInfo.Identity,
                    controlSemantics, rootSemantics, tokenProtectedSentinelName: null);
            }

            var recovered = new List<Guid>();
            var live = new List<Guid>();
            foreach (var plan in plans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (plan.SentinelName is null)
                {
                    // Only the two terminal journal phases are legal without a sentinel.
                    ReplayPlan(plan, expectedFiles, heldRoot, expectedRoot,
                        verifiedCompleteMembership.EnrollmentEpoch, control, controlInfo.Identity,
                        controlSemantics, rootSemantics, tokenProtectedSentinelName: null);
                    await RemoveTerminalRecordAsync(recovery, plan, expectedFiles, heldRoot, expectedRoot,
                        verifiedCompleteMembership.EnrollmentEpoch, control, controlInfo.Identity,
                        controlSemantics, rootSemantics).ConfigureAwait(false);
                    recovered.Add(plan.UseId);
                    cancellationToken.ThrowIfCancellationRequested();
                    continue;
                }

                var isLive = await ProbeSentinelAsync(plan, expectedFiles, heldRoot, expectedRoot,
                    verifiedCompleteMembership.EnrollmentEpoch, control, controlInfo.Identity,
                    controlSemantics, rootSemantics, cancellationToken).ConfigureAwait(false);
                if (isLive)
                {
                    if (plan.IsPublished && plan.RecordName is not null &&
                        !string.Equals(plan.RecordName, RecordName(plan.UseId), StringComparison.Ordinal))
                    {
                        ReplayPlan(plan, expectedFiles, heldRoot, expectedRoot,
                            verifiedCompleteMembership.EnrollmentEpoch, control, controlInfo.Identity,
                            controlSemantics, rootSemantics, tokenProtectedSentinelName: null);
                        MoveExpectedFile(recovery, plan.RecordName, RecordName(plan.UseId), expectedFiles,
                            heldRoot, expectedRoot, control, controlInfo.Identity, controlSemantics,
                            rootSemantics, tokenProtectedSentinelName: null);
                        plan.RecordName = RecordName(plan.UseId);
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    live.Add(plan.UseId);
                    continue;
                }

                // Probe cleanup is descriptive only. A new native token and replay are required below.
                ReplayPlan(plan, expectedFiles, heldRoot, expectedRoot,
                    verifiedCompleteMembership.EnrollmentEpoch, control, controlInfo.Identity,
                    controlSemantics, rootSemantics, tokenProtectedSentinelName: null);
                var sentinel = expectedFiles[plan.SentinelName];
                var removal = await recovery.TryOpenAndLockControlFileForRemovalAt(
                    control, plan.SentinelName, sentinel.Identity).ConfigureAwait(false);
                if (removal is null)
                {
                    // Ownership became live after the ordinary probe. Restore published names for ordinary readers.
                    ReplayPlan(plan, expectedFiles, heldRoot, expectedRoot,
                        verifiedCompleteMembership.EnrollmentEpoch, control, controlInfo.Identity,
                        controlSemantics, rootSemantics, tokenProtectedSentinelName: null);
                    if (plan.IsPublished && plan.RecordName is not null &&
                        !string.Equals(plan.RecordName, RecordName(plan.UseId), StringComparison.Ordinal))
                    {
                        MoveExpectedFile(recovery, plan.RecordName, RecordName(plan.UseId), expectedFiles,
                            heldRoot, expectedRoot, control, controlInfo.Identity, controlSemantics,
                            rootSemantics, tokenProtectedSentinelName: null);
                        plan.RecordName = RecordName(plan.UseId);
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    live.Add(plan.UseId);
                    continue;
                }

                await using (removal.ConfigureAwait(false))
                {
                    var expectedCanonicalName = new PhysicalStoreCanonicalName(
                        controlInfo.Identity, sentinel.Identity, plan.SentinelName, controlSemantics);
                    if (removal.CanonicalName != expectedCanonicalName)
                        throw Refused("The fresh removal token does not bind the exact expected sentinel name and identity.");

                    if (plan.IsPublished)
                    {
                        if (plan.RecordName is null)
                            throw Refused("A published graph-use recovery plan lost its exact record name.");
                        if (!string.Equals(plan.RecordName, DeletingName(plan.UseId), StringComparison.Ordinal))
                        {
                            if (!string.Equals(plan.RecordName, ReapingName(plan.UseId), StringComparison.Ordinal))
                            {
                                ReplayPlan(plan, expectedFiles, heldRoot, expectedRoot,
                                    verifiedCompleteMembership.EnrollmentEpoch, control, controlInfo.Identity,
                                    controlSemantics, rootSemantics, tokenProtectedSentinelName: plan.SentinelName,
                                    tokenFileSystem: recovery, token: removal);
                                MoveExpectedFile(recovery, plan.RecordName, ReapingName(plan.UseId), expectedFiles,
                                    heldRoot, expectedRoot, control, controlInfo.Identity, controlSemantics,
                                    rootSemantics, tokenProtectedSentinelName: plan.SentinelName,
                                    tokenFileSystem: recovery, token: removal);
                                plan.RecordName = ReapingName(plan.UseId);
                                cancellationToken.ThrowIfCancellationRequested();
                            }

                            ReplayPlan(plan, expectedFiles, heldRoot, expectedRoot,
                                verifiedCompleteMembership.EnrollmentEpoch, control, controlInfo.Identity,
                                controlSemantics, rootSemantics, tokenProtectedSentinelName: plan.SentinelName,
                                tokenFileSystem: recovery, token: removal);
                            MoveExpectedFile(recovery, plan.RecordName, DeletingName(plan.UseId), expectedFiles,
                                heldRoot, expectedRoot, control, controlInfo.Identity, controlSemantics,
                                rootSemantics, tokenProtectedSentinelName: plan.SentinelName,
                                tokenFileSystem: recovery, token: removal);
                            plan.RecordName = DeletingName(plan.UseId);
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }
                    else if (plan.StageName is not null &&
                             !string.Equals(plan.StageName, StageDeletingName(plan.UseId), StringComparison.Ordinal))
                    {
                        ReplayPlan(plan, expectedFiles, heldRoot, expectedRoot,
                            verifiedCompleteMembership.EnrollmentEpoch, control, controlInfo.Identity,
                            controlSemantics, rootSemantics, tokenProtectedSentinelName: plan.SentinelName,
                            tokenFileSystem: recovery, token: removal);
                        MoveExpectedFile(recovery, plan.StageName, StageDeletingName(plan.UseId), expectedFiles,
                            heldRoot, expectedRoot, control, controlInfo.Identity, controlSemantics,
                            rootSemantics, tokenProtectedSentinelName: plan.SentinelName,
                            tokenFileSystem: recovery, token: removal);
                        plan.StageName = StageDeletingName(plan.UseId);
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    ReplayPlan(plan, expectedFiles, heldRoot, expectedRoot,
                        verifiedCompleteMembership.EnrollmentEpoch, control, controlInfo.Identity,
                        controlSemantics, rootSemantics, tokenProtectedSentinelName: plan.SentinelName,
                        tokenFileSystem: recovery, token: removal);
                    var sentinelNow = expectedFiles[plan.SentinelName];
                    if (sentinelNow.Identity != removal.CanonicalName.FileIdentity || sentinelNow.Length != 0)
                        throw Refused("The fresh removal token and current graph-use sentinel evidence disagree.");
                    await recovery.RemoveLockedControlFileAsync(removal).ConfigureAwait(false);
                    var removedSentinelName = plan.SentinelName;
                    expectedFiles.Remove(removedSentinelName);
                    VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics,
                        control, controlInfo.Identity, controlSemantics);
                    VerifyExpectedNamespace(control, controlInfo.Identity, controlSemantics,
                        expectedRoot, expectedFiles, tokenProtectedSentinelName: null);
                    RequireAbsent(control, removedSentinelName);
                    plan.SentinelName = null;
                }

                // The token has closed and released its parent lease. The journal is now terminal and
                // record-only removal can be retried after interruption without consulting stale memory.
                if (plan.RecordName is not null)
                {
                    await RemoveTerminalRecordAsync(recovery, plan, expectedFiles, heldRoot, expectedRoot,
                        verifiedCompleteMembership.EnrollmentEpoch, control, controlInfo.Identity,
                        controlSemantics, rootSemantics).ConfigureAwait(false);
                }
                else if (plan.StageName is not null)
                {
                    await RemoveTerminalRecordAsync(recovery, plan, expectedFiles, heldRoot, expectedRoot,
                        verifiedCompleteMembership.EnrollmentEpoch, control, controlInfo.Identity,
                        controlSemantics, rootSemantics).ConfigureAwait(false);
                }

                recovered.Add(plan.UseId);
                cancellationToken.ThrowIfCancellationRequested();
            }

            VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics, control, controlInfo.Identity, controlSemantics);
            VerifyExpectedNamespace(control, controlInfo.Identity, controlSemantics,
                expectedRoot, expectedFiles, tokenProtectedSentinelName: null);
            cancellationToken.ThrowIfCancellationRequested();
            return new GraphUseRecordRecoveryResult(recovered, live);
        }
        finally
        {
            control?.Dispose();
        }
    }

    private Dictionary<string, RecoveryArtifact> CaptureRecoveryNamespace(
        PhysicalStoreDirectoryHandle control,
        PhysicalFileIdentity controlIdentity,
        PhysicalStoreNameSemantics controlSemantics,
        PhysicalRootIdentity expectedRoot,
        IReadOnlyList<UseArtifactNames> plans)
    {
        var budget = new InspectionBudget(_maximumTotalUseRecordBytes);
        var names = plans.SelectMany(static plan => plan.ArtifactNames())
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        var result = new Dictionary<string, RecoveryArtifact>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var entry = RequireUseEntry(control, name, expectedIdentity: null);
            var isSentinel = name.EndsWith(".sentinel", StringComparison.Ordinal);
            var isStage = name.EndsWith(".json.stage", StringComparison.Ordinal) ||
                          name.EndsWith(".stage-deleting", StringComparison.Ordinal);
            var maximumBytes = isSentinel ? 0 : GraphUsePayloadSerializer.MaximumPayloadBytes;
            if ((isSentinel && entry.Length != 0) ||
                (!isSentinel && isStage && entry.Length > maximumBytes) ||
                (!isSentinel && !isStage && entry.Length is <= 0 or > GraphUsePayloadSerializer.MaximumPayloadBytes))
            {
                throw Refused("A graph-use control artifact has an empty, oversized, or otherwise unsupported length.");
            }

            RequireControlFile(entry, entry.Identity, expectedRoot.HandleIdentity, entry.Length,
                "A graph-use control artifact is not a regular single-link file on the expected root volume.");
            VerifyCanonicalFile(control, controlIdentity, controlSemantics, name, entry.Identity, entry.Length);
            budget.AddArtifactBytes(checked((int)entry.Length), allowEmpty: isStage || isSentinel);
            var payload = ReadExactFile(control, controlIdentity, controlSemantics, name, entry.Identity,
                expectedRoot.HandleIdentity, maximumBytes);
            result.Add(name, new RecoveryArtifact(name, entry.Identity, entry.Length, payload));
        }

        VerifyExpectedNamespace(control, controlIdentity, controlSemantics, expectedRoot, result, tokenProtectedSentinelName: null);
        return result;
    }

    private RecoveryPlan InitializeRecoveryPlan(
        UseArtifactNames names,
        IReadOnlyDictionary<string, RecoveryArtifact> artifacts,
        PhysicalStoreDirectoryHandle heldRoot,
        PhysicalRootIdentity expectedRoot,
        long enrollmentEpoch)
    {
        var plan = new RecoveryPlan(names.UseId)
        {
            SentinelName = names.SentinelName,
            RecordName = names.RecordName ?? names.ReapingName ?? names.DeletingName,
            StageName = names.StageName ?? names.StageDeletingName
        };

        if (plan.RecordName is not null)
        {
            var file = artifacts[plan.RecordName];
            plan.RecordPayload = file.Payload;
            plan.Record = DeserializeAndValidateRecord(file.Payload, plan.UseId, expectedRoot, enrollmentEpoch);
            if (plan.Record.SentinelIdentity == file.Identity ||
                !SameVolume(plan.Record.SentinelIdentity, expectedRoot.HandleIdentity))
            {
                throw Refused("A graph-use record contains an invalid sentinel identity.");
            }

            if (plan.SentinelName is null)
            {
                if (!string.Equals(plan.RecordName, DeletingName(plan.UseId), StringComparison.Ordinal))
                    throw Refused("A published graph-use record without its sentinel lacks the terminal deleting journal.");
            }
            else if (artifacts[plan.SentinelName].Identity != plan.Record.SentinelIdentity)
            {
                throw Refused("A graph-use record does not bind the exact present sentinel identity.");
            }

            ReplayInstallIdentities(heldRoot, expectedRoot, plan.Record.GraphSnapshot);
        }
        else if (plan.StageName is not null)
        {
            plan.StagePayload = artifacts[plan.StageName].Payload;
            if (plan.SentinelName is null && !string.Equals(plan.StageName, StageDeletingName(plan.UseId), StringComparison.Ordinal))
                throw Refused("An unpublished stage without its sentinel lacks the terminal stage-deleting journal.");
        }
        else if (plan.SentinelName is null)
        {
            throw Refused("A graph-use recovery plan contains no record, stage, or sentinel artifact.");
        }

        VerifyRoot(heldRoot, expectedRoot);
        return plan;
    }

    private async Task<bool> ProbeSentinelAsync(
        RecoveryPlan plan,
        Dictionary<string, RecoveryArtifact> expectedFiles,
        PhysicalStoreDirectoryHandle heldRoot,
        PhysicalRootIdentity expectedRoot,
        long enrollmentEpoch,
        PhysicalStoreDirectoryHandle control,
        PhysicalFileIdentity controlIdentity,
        PhysicalStoreNameSemantics controlSemantics,
        PhysicalStoreNameSemantics rootSemantics,
        CancellationToken cancellationToken)
    {
        var sentinelName = plan.SentinelName ?? throw new InvalidOperationException("The recovery plan has no sentinel.");
        var sentinelSnapshot = expectedFiles[sentinelName];
        VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);
        VerifyExpectedNamespace(control, controlIdentity, controlSemantics,
            expectedRoot, expectedFiles, tokenProtectedSentinelName: null);
        var sentinel = _files.OpenFileChildNoFollow(control, sentinelName, FileAccess.ReadWrite);
        IAsyncDisposable? nativeLock = null;
        var lockReleaseConfirmed = true;
        Exception? primaryFailure = null;
        var isLive = false;
        try
        {
            VerifyHeldSentinel(control, controlIdentity, controlSemantics, sentinel, sentinelName,
                sentinelSnapshot.Identity, expectedRoot.HandleIdentity);
            VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);
            cancellationToken.ThrowIfCancellationRequested();
            nativeLock = await _files.TryAcquireExclusiveLock(sentinel).ConfigureAwait(false);
            isLive = nativeLock is null;
            ReplayPlan(plan, expectedFiles, heldRoot, expectedRoot, enrollmentEpoch,
                control, controlIdentity, controlSemantics, rootSemantics, tokenProtectedSentinelName: null);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        if (nativeLock is not null)
        {
            try
            {
                await nativeLock.DisposeAsync().ConfigureAwait(false);
                nativeLock = null;
            }
            catch (Exception exception)
            {
                lockReleaseConfirmed = false;
                primaryFailure = primaryFailure is null ? exception : new AggregateException(primaryFailure, exception);
            }
        }

        if (lockReleaseConfirmed)
        {
            try { sentinel.Dispose(); }
            catch (Exception exception)
            {
                primaryFailure = primaryFailure is null ? exception : new AggregateException(primaryFailure, exception);
            }
        }

        if (primaryFailure is not null)
        {
            if (!lockReleaseConfirmed && nativeLock is not null)
                primaryFailure.Data["UnreleasedGraphUseProbeOwnership"] = new ProbeOwnership(sentinel, nativeLock);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);
        VerifyExpectedNamespace(control, controlIdentity, controlSemantics,
            expectedRoot, expectedFiles, tokenProtectedSentinelName: null);
        cancellationToken.ThrowIfCancellationRequested();
        return isLive;
    }

    private void ReplayPlan(
        RecoveryPlan plan,
        Dictionary<string, RecoveryArtifact> expectedFiles,
        PhysicalStoreDirectoryHandle heldRoot,
        PhysicalRootIdentity expectedRoot,
        long enrollmentEpoch,
        PhysicalStoreDirectoryHandle control,
        PhysicalFileIdentity controlIdentity,
        PhysicalStoreNameSemantics controlSemantics,
        PhysicalStoreNameSemantics rootSemantics,
        string? tokenProtectedSentinelName,
        IPhysicalStoreControlRecoveryFileSystem? tokenFileSystem = null,
        PhysicalStoreLockedControlFile? token = null)
    {
        VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);
        VerifyExpectedNamespace(control, controlIdentity, controlSemantics,
            expectedRoot, expectedFiles, tokenProtectedSentinelName, tokenFileSystem, token);

        if (plan.RecordName is not null)
        {
            var expected = expectedFiles[plan.RecordName];
            var payload = ReadExactFile(control, controlIdentity, controlSemantics, plan.RecordName,
                expected.Identity, expectedRoot.HandleIdentity, GraphUsePayloadSerializer.MaximumPayloadBytes);
            if (!payload.AsSpan().SequenceEqual(plan.RecordPayload))
                throw Refused("A graph-use record changed from its fully validated recovery payload.");
            var record = DeserializeAndValidateRecord(payload, plan.UseId, expectedRoot, enrollmentEpoch);
            if (record.SentinelIdentity != plan.Record!.SentinelIdentity)
                throw Refused("A graph-use record changed its bound root, epoch, graph, or sentinel identity during recovery.");

            if (plan.SentinelName is null)
            {
                RequireAbsent(control, SentinelName(plan.UseId));
                if (!string.Equals(plan.RecordName, DeletingName(plan.UseId), StringComparison.Ordinal))
                    throw Refused("A record without a sentinel is outside the selected terminal recovery phase.");
            }
            else
            {
                var sentinel = expectedFiles[plan.SentinelName];
                if (sentinel.Identity != record.SentinelIdentity || sentinel.Length != 0)
                    throw Refused("The published graph-use record and its exact empty sentinel disagree.");
            }

            ReplayInstallIdentities(heldRoot, expectedRoot, record.GraphSnapshot);
        }
        else if (plan.StageName is not null)
        {
            var expected = expectedFiles[plan.StageName];
            var payload = ReadExactFile(control, controlIdentity, controlSemantics, plan.StageName,
                expected.Identity, expectedRoot.HandleIdentity, GraphUsePayloadSerializer.MaximumPayloadBytes);
            if (!payload.AsSpan().SequenceEqual(plan.StagePayload))
                throw Refused("An unpublished graph-use stage changed from its bounded recovery bytes.");
            if (plan.SentinelName is null)
            {
                RequireAbsent(control, SentinelName(plan.UseId));
                if (!string.Equals(plan.StageName, StageDeletingName(plan.UseId), StringComparison.Ordinal))
                    throw Refused("A stage without a sentinel is outside the selected terminal recovery phase.");
            }
            else if (expectedFiles[plan.SentinelName].Length != 0)
            {
                throw Refused("An unpublished stage sentinel is not empty.");
            }
        }
        else if (plan.SentinelName is not null)
        {
            var sentinel = expectedFiles[plan.SentinelName];
            if (sentinel.Length != 0)
                throw Refused("A sentinel-only graph-use artifact is not empty.");
        }
    }

    private void ReplayInstallIdentities(
        PhysicalStoreDirectoryHandle heldRoot,
        PhysicalRootIdentity expectedRoot,
        ProtectedGraphSnapshot graphSnapshot)
    {
        var observations = new List<PackageInstallIdentityReader.PackageInstallIdentityObservation>();
        try
        {
            ObserveGraphInstalls(heldRoot, expectedRoot, graphSnapshot, observations);
            foreach (var observation in observations)
                observation.Revalidate();
        }
        finally
        {
            for (var index = observations.Count - 1; index >= 0; index--)
                observations[index].Dispose();
        }
    }

    private void VerifyExpectedNamespace(
        PhysicalStoreDirectoryHandle control,
        PhysicalFileIdentity controlIdentity,
        PhysicalStoreNameSemantics controlSemantics,
        PhysicalRootIdentity expectedRoot,
        IReadOnlyDictionary<string, RecoveryArtifact> expectedFiles,
        string? tokenProtectedSentinelName,
        IPhysicalStoreControlRecoveryFileSystem? tokenFileSystem = null,
        PhysicalStoreLockedControlFile? token = null)
    {
        var names = EnumerateUseArtifactNames(control);
        var expectedNames = expectedFiles.Keys.OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        if (!names.SequenceEqual(expectedNames, StringComparer.Ordinal))
            throw Refused("The complete graph-use namespace changed during control-artifact recovery.");

        foreach (var (name, expected) in expectedFiles)
        {
            PhysicalStoreEntryInfo info;
            if (string.Equals(name, tokenProtectedSentinelName, StringComparison.Ordinal))
            {
                if (tokenFileSystem is null || token is null || token.CanonicalName.Basename != name ||
                    token.CanonicalName.FileIdentity != expected.Identity)
                {
                    throw Refused("A token-protected graph-use entry lacks its exact active native inspection capability.");
                }

                info = tokenFileSystem.InspectLockedControlFile(token);
            }
            else
            {
                info = RequireUseEntry(control, name, expected.Identity);
            }

            RequireControlFile(info, expected.Identity, expectedRoot.HandleIdentity, expected.Length,
                "A graph-use control artifact changed type, link count, identity, length, or root volume during recovery.");
            if (!string.Equals(name, tokenProtectedSentinelName, StringComparison.Ordinal))
                VerifyCanonicalFile(control, controlIdentity, controlSemantics, name, expected.Identity, expected.Length);
        }

    }

    private void MoveExpectedFile(
        IPhysicalStoreControlRecoveryFileSystem recovery,
        string sourceName,
        string destinationName,
        Dictionary<string, RecoveryArtifact> expectedFiles,
        PhysicalStoreDirectoryHandle heldRoot,
        PhysicalRootIdentity expectedRoot,
        PhysicalStoreDirectoryHandle control,
        PhysicalFileIdentity controlIdentity,
        PhysicalStoreNameSemantics controlSemantics,
        PhysicalStoreNameSemantics rootSemantics,
        string? tokenProtectedSentinelName,
        IPhysicalStoreControlRecoveryFileSystem? tokenFileSystem = null,
        PhysicalStoreLockedControlFile? token = null)
    {
        if (!expectedFiles.TryGetValue(sourceName, out var source) || expectedFiles.ContainsKey(destinationName))
            throw Refused("A graph-use recovery move does not have one exact source and an absent destination.");
        VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);
        VerifyExpectedNamespace(control, controlIdentity, controlSemantics,
            expectedRoot, expectedFiles, tokenProtectedSentinelName, tokenFileSystem, token);
        var moved = recovery.MoveControlFileNoReplaceAt(control, sourceName, source.Identity, destinationName);
        if (moved.Kind != PhysicalStoreEntryKind.RegularFile || moved.LinkCount != 1 || moved.Identity != source.Identity ||
            moved.Length != source.Length || !SameVolume(moved.Identity, expectedRoot.HandleIdentity))
        {
            throw Refused("A graph-use recovery journal move did not preserve the exact regular-file identity and length.");
        }

        expectedFiles.Remove(sourceName);
        expectedFiles.Add(destinationName, source with { Name = destinationName });
        VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);
        VerifyExpectedNamespace(control, controlIdentity, controlSemantics,
            expectedRoot, expectedFiles, tokenProtectedSentinelName, tokenFileSystem, token);
    }

    private async Task RemoveTerminalRecordAsync(
        IPhysicalStoreControlRecoveryFileSystem recovery,
        RecoveryPlan plan,
        Dictionary<string, RecoveryArtifact> expectedFiles,
        PhysicalStoreDirectoryHandle heldRoot,
        PhysicalRootIdentity expectedRoot,
        long enrollmentEpoch,
        PhysicalStoreDirectoryHandle control,
        PhysicalFileIdentity controlIdentity,
        PhysicalStoreNameSemantics controlSemantics,
        PhysicalStoreNameSemantics rootSemantics)
    {
        var name = plan.RecordName ?? plan.StageName ??
            throw Refused("A terminal graph-use cleanup has no exact record or stage artifact.");
        if (plan.RecordName is not null && !string.Equals(name, DeletingName(plan.UseId), StringComparison.Ordinal) ||
            plan.StageName is not null && !string.Equals(name, StageDeletingName(plan.UseId), StringComparison.Ordinal))
        {
            throw Refused("A graph-use record or stage is not in its terminal deletion journal phase.");
        }

        ReplayPlan(plan, expectedFiles, heldRoot, expectedRoot, enrollmentEpoch,
            control, controlIdentity, controlSemantics, rootSemantics, tokenProtectedSentinelName: null);
        RequireAbsent(control, SentinelName(plan.UseId));
        var artifact = expectedFiles[name];
        var removal = await recovery.TryOpenAndLockControlFileForRemovalAt(control, name, artifact.Identity).ConfigureAwait(false);
        if (removal is null)
            throw Refused("A terminal graph-use journal is unexpectedly busy; its evidence was preserved for retry.");

        await using (removal.ConfigureAwait(false))
        {
            var expectedCanonicalName = new PhysicalStoreCanonicalName(
                controlIdentity, artifact.Identity, name, controlSemantics);
            if (removal.CanonicalName != expectedCanonicalName)
                throw Refused("The fresh terminal cleanup token does not bind the exact journal identity and name.");
            VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics,
                control, controlIdentity, controlSemantics);
            VerifyExpectedNamespace(control, controlIdentity, controlSemantics, expectedRoot,
                expectedFiles, tokenProtectedSentinelName: name, tokenFileSystem: recovery, token: removal);
            var lockedArtifact = recovery.InspectLockedControlFile(removal);
            RequireControlFile(lockedArtifact, artifact.Identity, expectedRoot.HandleIdentity, artifact.Length,
                "The terminal journal removal token no longer identifies the exact regular file.");
            if (plan.Record is not null)
                ReplayInstallIdentities(heldRoot, expectedRoot, plan.Record.GraphSnapshot);
            await recovery.RemoveLockedControlFileAsync(removal).ConfigureAwait(false);
        }

        expectedFiles.Remove(name);
        VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);
        VerifyExpectedNamespace(control, controlIdentity, controlSemantics,
            expectedRoot, expectedFiles, tokenProtectedSentinelName: null);
        RequireAbsent(control, name);
    }

    private void RequireAbsent(PhysicalStoreDirectoryHandle control, string name)
    {
        PhysicalStoreNames.ValidateSingleComponent(name);
        if (_files.InspectChildNoFollow(control, name) is not null)
            throw Refused("A graph-use recovery artifact expected to be absent is still present.");
    }

    private static string RecordName(Guid useId) => $"use-{useId:N}.json";
    private static string SentinelName(Guid useId) => $"use-{useId:N}.sentinel";
    private static string ReapingName(Guid useId) => $"use-{useId:N}.reaping";
    private static string DeletingName(Guid useId) => $"use-{useId:N}.deleting";
    private static string StageDeletingName(Guid useId) => $"use-{useId:N}.stage-deleting";

    private sealed record RecoveryArtifact(string Name, PhysicalFileIdentity Identity, long Length, byte[] Payload);

    private sealed class RecoveryPlan(Guid useId)
    {
        internal Guid UseId { get; } = useId;
        internal string? SentinelName { get; set; }
        internal string? RecordName { get; set; }
        internal string? StageName { get; set; }
        internal byte[]? RecordPayload { get; set; }
        internal byte[]? StagePayload { get; set; }
        internal GraphUseRecord? Record { get; set; }
        internal bool IsPublished => RecordName is not null;
    }

    private sealed record ProbeOwnership(PhysicalStoreFileHandle File, IAsyncDisposable Lock);
}

/// <summary>Immutable result of one bounded graph-use control-artifact recovery pass.</summary>
internal sealed class GraphUseRecordRecoveryResult
{
    internal GraphUseRecordRecoveryResult(IEnumerable<Guid> recoveredUseIds, IEnumerable<Guid> liveUseIds)
    {
        ArgumentNullException.ThrowIfNull(recoveredUseIds);
        ArgumentNullException.ThrowIfNull(liveUseIds);
        RecoveredUseIds = Array.AsReadOnly(recoveredUseIds.ToArray());
        LiveUseIds = Array.AsReadOnly(liveUseIds.ToArray());
    }

    internal IReadOnlyList<Guid> RecoveredUseIds { get; }
    internal IReadOnlyList<Guid> LiveUseIds { get; }
}
