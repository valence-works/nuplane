using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.Coordination.GraphUseSerialization;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Coordination;

internal sealed partial class PackageGraphUseRecordStore
{
    private const int MaximumUseDirectoryEntries = 4096;
    // The per-record serializer bound multiplied by the namespace count would otherwise retain several GiB.

    /// <summary>Inspects graph-use records under an already-validated Complete root operation.</summary>
    /// <remarks>
    /// The caller must retain the root and all member locks and must have independently revalidated the current
    /// Complete membership and every enrolled state. This method only classifies immutable use-record evidence.
    /// It never removes control files, reads package payloads, or returns package access or deletion authority.
    /// </remarks>
    internal async Task<GraphUseRecordInspectionResult> InspectAsync(
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

        PhysicalStoreDirectoryHandle? control = null;
        GraphUseRecordInspectionResult? result = null;
        Exception? primaryFailure = null;
        var cleanupFailures = new List<Exception>();
        var unreleasedResources = new List<object>();
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

            var initialNames = EnumerateUseArtifactNames(control);
            var uses = ParseUseArtifactNames(initialNames);
            var entries = new List<GraphUseRecordInspectionEntry>(uses.Count);
            var budget = new InspectionBudget(_maximumTotalUseRecordBytes);
            foreach (var use in uses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(await InspectUseAsync(
                    heldRoot,
                    expectedRoot,
                    verifiedCompleteMembership.EnrollmentEpoch,
                    control,
                    controlInfo.Identity,
                    controlSemantics,
                    rootSemantics,
                    use,
                    budget,
                    cancellationToken).ConfigureAwait(false));
            }

            cancellationToken.ThrowIfCancellationRequested();
            VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics, control, controlInfo.Identity, controlSemantics);
            var finalNames = EnumerateUseArtifactNames(control);
            if (!initialNames.SequenceEqual(finalNames, StringComparer.Ordinal))
                throw Refused("The graph-use namespace changed while its records were being classified.");
            VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics, control, controlInfo.Identity, controlSemantics);

            result = new GraphUseRecordInspectionResult(
                expectedRoot,
                verifiedCompleteMembership.EnrollmentEpoch,
                entries.OrderBy(static entry => entry.Record.UseId).ToArray());
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        if (control is not null)
        {
            try { control.Dispose(); }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
                unreleasedResources.Add(control);
            }
        }

        if (cleanupFailures.Count > 0)
        {
            IEnumerable<Exception> failures = primaryFailure is null
                ? cleanupFailures
                : new[] { primaryFailure }.Concat(cleanupFailures);
            var aggregate = new AggregateException(
                "Graph-use inspection failed and one or more held-directory resources could not be released with a confirmed outcome.",
                failures);
            if (unreleasedResources.Count > 0)
                aggregate.Data["UnreleasedGraphUseInspectionResources"] = unreleasedResources.ToArray();
            throw aggregate;
        }

        if (primaryFailure is not null)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();

        return result ?? throw new InvalidOperationException("Graph-use inspection completed without a result.");
    }

    private async Task<GraphUseRecordInspectionEntry> InspectUseAsync(
        PhysicalStoreDirectoryHandle heldRoot,
        PhysicalRootIdentity expectedRoot,
        long enrollmentEpoch,
        PhysicalStoreDirectoryHandle control,
        PhysicalFileIdentity controlIdentity,
        PhysicalStoreNameSemantics controlSemantics,
        PhysicalStoreNameSemantics rootSemantics,
        UseArtifactNames names,
        InspectionBudget budget,
        CancellationToken cancellationToken)
    {
        PhysicalStoreFileHandle? sentinelFile = null;
        IAsyncDisposable? sentinelLock = null;
        var lockReleaseConfirmed = true;
        var installObservations = new List<PackageInstallIdentityReader.PackageInstallIdentityObservation>();
        var unreleasedResources = new List<object>();
        Exception? primaryFailure = null;
        var cleanupFailures = new List<Exception>();
        GraphUseRecordInspectionEntry? result = null;

        try
        {
            var firstRead = ReadAndValidateUseRecord(
                control, controlIdentity, controlSemantics, expectedRoot, enrollmentEpoch, names,
                expectedRecordIdentity: null, expectedSentinelIdentity: null, budget);
            ObserveGraphInstalls(heldRoot, expectedRoot, firstRead.Record.GraphSnapshot, installObservations);
            cancellationToken.ThrowIfCancellationRequested();

            sentinelFile = _files.OpenFileChildNoFollow(control, names.SentinelName!, FileAccess.ReadWrite);
            VerifyHeldSentinel(control, controlIdentity, controlSemantics, sentinelFile, names.SentinelName!,
                firstRead.SentinelIdentity, expectedRoot.HandleIdentity);
            VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics,
                control, controlIdentity, controlSemantics);

            cancellationToken.ThrowIfCancellationRequested();
            sentinelLock = await _files.TryAcquireExclusiveLock(sentinelFile).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var replay = ReadAndValidateUseRecord(
                control, controlIdentity, controlSemantics, expectedRoot, enrollmentEpoch, names,
                firstRead.RecordIdentity, firstRead.SentinelIdentity);
            if (!firstRead.Payload.AsSpan().SequenceEqual(replay.Payload))
                throw Refused("The graph-use record payload changed while ownership was probed.");

            VerifyHeldSentinel(control, controlIdentity, controlSemantics, sentinelFile, names.SentinelName!,
                firstRead.SentinelIdentity, expectedRoot.HandleIdentity);
            foreach (var observation in installObservations)
                observation.Revalidate();
            cancellationToken.ThrowIfCancellationRequested();

            result = new GraphUseRecordInspectionEntry(
                firstRead.Record,
                sentinelLock is null ? GraphUseRecordOwnershipState.Live : GraphUseRecordOwnershipState.Stale);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        if (sentinelLock is not null)
        {
            try
            {
                await sentinelLock.DisposeAsync().ConfigureAwait(false);
                sentinelLock = null;
            }
            catch (Exception exception)
            {
                lockReleaseConfirmed = false;
                cleanupFailures.Add(exception);
            }
        }

        if (sentinelFile is not null && lockReleaseConfirmed)
        {
            try { sentinelFile.Dispose(); }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
                unreleasedResources.Add(sentinelFile);
            }
        }

        for (var index = installObservations.Count - 1; index >= 0; index--)
        {
            try { installObservations[index].Dispose(); }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
                unreleasedResources.Add(installObservations[index]);
            }
        }

        if (cleanupFailures.Count > 0)
        {
            IEnumerable<Exception> failures = primaryFailure is null
                ? cleanupFailures
                : new[] { primaryFailure }.Concat(cleanupFailures);
            var aggregate = new AggregateException(
                "Graph-use inspection failed and one or more native observations could not be released with a confirmed outcome.",
                failures);
            if (!lockReleaseConfirmed && sentinelFile is not null && sentinelLock is not null)
                aggregate.Data["UnreleasedGraphUseInspectionSentinelOwnership"] = new UnreleasedGraphUseSentinelOwnership(sentinelFile, sentinelLock);
            if (unreleasedResources.Count > 0)
                aggregate.Data["UnreleasedGraphUseInspectionResources"] = unreleasedResources.ToArray();
            throw aggregate;
        }

        if (primaryFailure is not null)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();

        return result ?? throw new InvalidOperationException("Graph-use record inspection completed without a classification.");
    }

    private RecordRead ReadAndValidateUseRecord(
        PhysicalStoreDirectoryHandle control,
        PhysicalFileIdentity controlIdentity,
        PhysicalStoreNameSemantics controlSemantics,
        PhysicalRootIdentity expectedRoot,
        long enrollmentEpoch,
        UseArtifactNames names,
        PhysicalFileIdentity? expectedRecordIdentity,
        PhysicalFileIdentity? expectedSentinelIdentity,
        InspectionBudget? budget = null)
    {
        var recordInfo = RequireUseEntry(control, names.RecordName!, expectedRecordIdentity);
        if (recordInfo.Length is <= 0 or > GraphUsePayloadSerializer.MaximumPayloadBytes)
            throw Refused("A graph-use record is empty or exceeds the supported bounded size.");
        RequireControlFile(recordInfo, expectedRecordIdentity, expectedRoot.HandleIdentity, recordInfo.Length,
            "The graph-use record is not a regular single-link file on the expected root volume.");
        VerifyCanonicalFile(control, controlIdentity, controlSemantics, names.RecordName!, recordInfo.Identity, recordInfo.Length);

        var sentinelInfo = RequireUseEntry(control, names.SentinelName!, expectedSentinelIdentity);
        RequireControlFile(sentinelInfo, expectedSentinelIdentity, expectedRoot.HandleIdentity, expectedLength: 0,
            "A graph-use sentinel is not an empty regular single-link file on the expected root volume.");
        VerifyCanonicalFile(control, controlIdentity, controlSemantics, names.SentinelName!, sentinelInfo.Identity, expectedLength: 0);
        if (sentinelInfo.Identity == recordInfo.Identity)
            throw Refused("The graph-use record and sentinel must be distinct native files.");

        // Reserve inventory bytes before opening or parsing this record's payload.
        budget?.AddRecordBytes(checked((int)recordInfo.Length));
        var payload = ReadExactFile(control, controlIdentity, controlSemantics, names.RecordName!, recordInfo.Identity,
            expectedRoot.HandleIdentity, checked((int)recordInfo.Length));
        if (payload.Length != recordInfo.Length)
            throw Refused("A graph-use record changed length after its inventory budget was reserved.");
        var record = DeserializeAndValidateRecord(payload, names.UseId, expectedRoot, enrollmentEpoch);
        if (record.SentinelIdentity != sentinelInfo.Identity ||
            record.GraphSnapshot.Roots.Count != 1 || record.GraphSnapshot.Roots[0] != expectedRoot ||
            record.GraphSnapshot.Nodes.Any(node => node.Install.Root != expectedRoot))
        {
            throw Refused("A graph-use record does not match its canonical name, Complete root epoch, sentinel, or single-root graph scope.");
        }

        return new RecordRead(record, recordInfo.Identity, sentinelInfo.Identity, payload);
    }

    private GraphUseRecord DeserializeAndValidateRecord(
        byte[] payload,
        Guid useId,
        PhysicalRootIdentity expectedRoot,
        long enrollmentEpoch)
    {
        GraphUseRecord record;
        try
        {
            record = _serializer.Deserialize(payload);
        }
        catch (JsonException exception)
        {
            throw new PackageStoreAdmissionException(
                PackageStoreAdmissionReason.UnknownAuthority,
                "A graph-use record is malformed or has an invalid canonical digest.",
                expectedRoot,
                exception);
        }

        if (record.UseId != useId || record.RootIdentity != expectedRoot || record.EnrollmentEpoch != enrollmentEpoch ||
            record.LifetimeKind != GraphUseLifetimeKind.OsExclusiveSentinel ||
            record.GraphSnapshot.Roots.Count != 1 || record.GraphSnapshot.Roots[0] != expectedRoot ||
            record.GraphSnapshot.Nodes.Any(node => node.Install.Root != expectedRoot))
        {
            throw Refused("A graph-use record does not match its canonical name, Complete root epoch, or single-root graph scope.");
        }

        return record;
    }

    private void ObserveGraphInstalls(
        PhysicalStoreDirectoryHandle heldRoot,
        PhysicalRootIdentity expectedRoot,
        ProtectedGraphSnapshot graphSnapshot,
        List<PackageInstallIdentityReader.PackageInstallIdentityObservation> observations)
    {
        var reader = new PackageInstallIdentityReader(_files);
        foreach (var node in graphSnapshot.Nodes)
        {
            var recorded = node.Install;
            if (recorded.Root != expectedRoot)
                throw Refused("A graph-use node names an install outside the one physical root being inspected.");

            var observation = reader.Observe(heldRoot, expectedRoot, recorded.RootRelativeInstallPath,
                recorded.PackageId, recorded.Version, recorded.VerifiedArchiveHash);
            observations.Add(observation);
            if (observation.InstallIdentity != recorded)
                throw Refused("A graph-use node's native install directory or completion marker no longer matches its record.");
        }
    }

    private PhysicalStoreEntryInfo RequireUseEntry(
        PhysicalStoreDirectoryHandle control,
        string name,
        PhysicalFileIdentity? expectedIdentity)
    {
        var info = _files.InspectChildNoFollow(control, name);
        if (info is null || (expectedIdentity is not null && info.Identity != expectedIdentity))
            throw Refused("A graph-use record or sentinel is missing or changed identity.");
        return info;
    }

    private void RequireCompleteBoundary(PhysicalRootIdentity expectedRoot, RootMembershipRecord membership)
    {
        if (membership.Status != RootMembershipStatus.Complete || membership.PendingStateCommit is not null ||
            membership.EnrollmentEpoch <= 0 || membership.RootIdentity != expectedRoot)
        {
            throw Refused("Graph-use inspection requires the caller's current Complete membership for the expected root and epoch.");
        }
    }

    private IReadOnlyList<string> EnumerateUseArtifactNames(PhysicalStoreDirectoryHandle control)
    {
        var allNames = _enumeration.EnumerateChildNamesNoFollow(control, MaximumUseDirectoryEntries);
        if (allNames.Count > MaximumUseDirectoryEntries || allNames.Any(static name => name is null))
            throw Refused("The native control-directory enumeration exceeded its bound or returned an invalid name.");
        return Array.AsReadOnly(allNames
            .Where(static name => name.StartsWith("use", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray());
    }

    private IReadOnlyList<UseArtifactNames> ParseUseArtifactNames(
        IReadOnlyList<string> artifactNames,
        bool allowRecoveryArtifacts = false)
    {
        var byId = new Dictionary<Guid, UseArtifactNames>();
        foreach (var name in artifactNames)
        {
            PhysicalStoreNames.ValidateSingleComponent(name);
            if (!name.StartsWith("use-", StringComparison.Ordinal) || name.Length < 4 + 32)
                throw Refused("A graph-use artifact has a noncanonical prefix or truncated identifier.");

            const int identifierStart = 4;
            var identifierLength = 32;
            var identifierText = name.AsSpan(identifierStart, identifierLength);
            if (!Guid.TryParseExact(identifierText, "N", out var useId) || useId == Guid.Empty ||
                !string.Equals(useId.ToString("N"), identifierText.ToString(), StringComparison.Ordinal))
            {
                throw Refused("A graph-use artifact has a malformed or noncanonical identifier.");
            }

            if (!byId.TryGetValue(useId, out var pair))
            {
                pair = new UseArtifactNames(useId);
                byId.Add(useId, pair);
            }

            if (string.Equals(name, $"use-{useId:N}.json", StringComparison.Ordinal))
            {
                if (pair.RecordName is not null)
                    throw Refused("A graph-use identifier has duplicate record names.");
                pair.RecordName = name;
            }
            else if (string.Equals(name, $"use-{useId:N}.sentinel", StringComparison.Ordinal))
            {
                if (pair.SentinelName is not null)
                    throw Refused("A graph-use identifier has duplicate sentinel names.");
                pair.SentinelName = name;
            }
            else if (allowRecoveryArtifacts && string.Equals(name, $"use-{useId:N}.json.stage", StringComparison.Ordinal))
            {
                if (pair.StageName is not null)
                    throw Refused("A graph-use identifier has duplicate staged payload names.");
                pair.StageName = name;
            }
            else if (allowRecoveryArtifacts && string.Equals(name, $"use-{useId:N}.reaping", StringComparison.Ordinal))
            {
                if (pair.ReapingName is not null)
                    throw Refused("A graph-use identifier has duplicate reaping record names.");
                pair.ReapingName = name;
            }
            else if (allowRecoveryArtifacts && string.Equals(name, $"use-{useId:N}.deleting", StringComparison.Ordinal))
            {
                if (pair.DeletingName is not null)
                    throw Refused("A graph-use identifier has duplicate deleting record names.");
                pair.DeletingName = name;
            }
            else if (allowRecoveryArtifacts && string.Equals(name, $"use-{useId:N}.stage-deleting", StringComparison.Ordinal))
            {
                if (pair.StageDeletingName is not null)
                    throw Refused("A graph-use identifier has duplicate deleting stage names.");
                pair.StageDeletingName = name;
            }
            else
            {
                throw Refused("A graph-use namespace contains an unknown, staged, or noncanonical artifact name.");
            }
        }

        foreach (var pair in byId.Values)
        {
            var publishedRecords = (pair.RecordName is null ? 0 : 1) +
                                   (pair.ReapingName is null ? 0 : 1) +
                                   (pair.DeletingName is null ? 0 : 1);
            var unpublishedRecords = (pair.StageName is null ? 0 : 1) + (pair.StageDeletingName is null ? 0 : 1);
            if (!allowRecoveryArtifacts)
            {
                if (pair.RecordName is null || pair.SentinelName is null)
                    throw Refused("A graph-use record and its sentinel must be present as one canonical pair.");
                continue;
            }

            if (publishedRecords + unpublishedRecords > 1 || (publishedRecords > 0 && unpublishedRecords > 0))
                throw Refused("A graph-use identifier has duplicate or mixed recovery phases.");

            var hasSentinel = pair.SentinelName is not null;
            var hasPublishedRecord = publishedRecords == 1;
            var hasUnpublishedRecord = unpublishedRecords == 1;
            var isRecordOnlyTerminalPhase = pair.DeletingName is not null || pair.StageDeletingName is not null;
            if ((hasPublishedRecord || hasUnpublishedRecord) && !hasSentinel && !isRecordOnlyTerminalPhase)
                throw Refused("A graph-use recovery phase is missing its sentinel before the terminal cleanup journal.");
            if (hasSentinel && !(hasPublishedRecord || hasUnpublishedRecord || publishedRecords == 0 && unpublishedRecords == 0))
                throw Refused("A graph-use sentinel does not match one supported recovery phase.");
            if (!hasSentinel && !isRecordOnlyTerminalPhase)
                throw Refused("A graph-use artifact has no sentinel and no terminal recovery journal.");
        }

        return Array.AsReadOnly(byId.Values.OrderBy(static pair => pair.UseId).ToArray());
    }

    private sealed class UseArtifactNames(Guid useId)
    {
        internal Guid UseId { get; } = useId;
        internal string? RecordName { get; set; }
        internal string? SentinelName { get; set; }
        internal string? StageName { get; set; }
        internal string? ReapingName { get; set; }
        internal string? DeletingName { get; set; }
        internal string? StageDeletingName { get; set; }

        internal IEnumerable<string> ArtifactNames()
        {
            if (RecordName is not null) yield return RecordName;
            if (SentinelName is not null) yield return SentinelName;
            if (StageName is not null) yield return StageName;
            if (ReapingName is not null) yield return ReapingName;
            if (DeletingName is not null) yield return DeletingName;
            if (StageDeletingName is not null) yield return StageDeletingName;
        }
    }

    private sealed record RecordRead(
        GraphUseRecord Record,
        PhysicalFileIdentity RecordIdentity,
        PhysicalFileIdentity SentinelIdentity,
        byte[] Payload);

    private sealed class InspectionBudget
    {
        private readonly long _maximumBytes;
        private long _totalRecordBytes;

        internal InspectionBudget(long maximumBytes) => _maximumBytes = maximumBytes;

        internal void AddRecordBytes(int bytes)
            => AddArtifactBytes(bytes, allowEmpty: false);

        internal void AddArtifactBytes(int bytes, bool allowEmpty)
        {
            if (bytes < 0 || (!allowEmpty && bytes == 0) || bytes > _maximumBytes - _totalRecordBytes)
                throw Refused("The graph-use inventory exceeds its cumulative bounded payload size.");

            _totalRecordBytes += bytes;
        }
    }
}

/// <summary>Descriptive result of one nonblocking operating-system sentinel probe.</summary>
/// <remarks>A stale classification alone never authorizes record cleanup or package deletion.</remarks>
internal enum GraphUseRecordOwnershipState
{
    Live = 1,
    Stale = 2
}

/// <summary>Detached use-record metadata and its sentinel-probe classification.</summary>
internal sealed record GraphUseRecordInspectionEntry(
    GraphUseRecord Record,
    GraphUseRecordOwnershipState OwnershipState);

/// <summary>Detached descriptive classifications of records observed beneath one held physical root.</summary>
/// <remarks>This value conveys neither install-read nor cleanup/deletion authority.</remarks>
internal sealed class GraphUseRecordInspectionResult
{
    internal GraphUseRecordInspectionResult(
        PhysicalRootIdentity rootIdentity,
        long enrollmentEpoch,
        IReadOnlyList<GraphUseRecordInspectionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(rootIdentity);
        ArgumentNullException.ThrowIfNull(entries);
        RootIdentity = ProtectionRecordValueCopies.CopyRoot(rootIdentity);
        EnrollmentEpoch = enrollmentEpoch;
        Entries = new ReadOnlyCollection<GraphUseRecordInspectionEntry>(entries.ToArray());
    }

    internal PhysicalRootIdentity RootIdentity { get; }
    internal long EnrollmentEpoch { get; }
    internal IReadOnlyList<GraphUseRecordInspectionEntry> Entries { get; }
}
