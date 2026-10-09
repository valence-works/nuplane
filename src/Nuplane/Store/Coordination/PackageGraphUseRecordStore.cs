using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.Coordination.GraphUseSerialization;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Coordination;

/// <summary>Publishes one immutable graph-use candidate beneath an already-held and validated root.</summary>
/// <remarks>
/// The caller must already own the root and every member lock and must have validated graph completeness and
/// install identities. This class takes only the graph-use sentinel lock. It returns no package-read capability,
/// does not inspect install paths, and never removes publication artifacts.
/// </remarks>
internal sealed class PackageGraphUseRecordStore
{
    private readonly IPhysicalStoreFileSystem _files;
    private readonly IPhysicalStoreNameFileSystem _names;
    private readonly IPhysicalStorePublicationFileSystem _publication;
    private readonly IPhysicalStoreDirectoryPublicationFileSystem _directoryNames;
    private readonly GraphUsePayloadSerializer _serializer = new();

    internal PackageGraphUseRecordStore(IPhysicalStoreFileSystem files)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files = files;
        _names = files as IPhysicalStoreNameFileSystem ?? throw Refused("The filesystem provider lacks native name observations.");
        _publication = files as IPhysicalStorePublicationFileSystem ?? throw Refused("The filesystem provider lacks atomic no-replace publication.");
        _directoryNames = files as IPhysicalStoreDirectoryPublicationFileSystem ?? throw Refused("The filesystem provider lacks native directory-name observations.");
    }

    /// <summary>Creates and publishes a graph-use record while the caller retains its root/member locks.</summary>
    internal async Task<PublishedGraphUseRecord> PublishAsync(
        PhysicalStoreDirectoryHandle heldRoot,
        PhysicalRootIdentity expectedRoot,
        long enrollmentEpoch,
        ProtectedGraphSnapshot graphSnapshot,
        PackageGraphUseSnapshotState snapshotState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(heldRoot);
        ArgumentNullException.ThrowIfNull(expectedRoot);
        ArgumentNullException.ThrowIfNull(graphSnapshot);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCandidate(expectedRoot, enrollmentEpoch, graphSnapshot, snapshotState);

        PhysicalStoreDirectoryHandle? control = null;
        PhysicalStoreFileHandle? sentinelFile = null;
        IAsyncDisposable? sentinelLock = null;
        try
        {
            var rootSemantics = VerifyRoot(heldRoot, expectedRoot);
            var controlEntry = _files.InspectChildNoFollow(heldRoot, RootMembershipRegistry.ControlDirectoryName);
            if (controlEntry is null || controlEntry.Kind != PhysicalStoreEntryKind.Directory ||
                !SameVolume(controlEntry.Identity, expectedRoot.HandleIdentity))
                throw Refused("The reserved control directory is missing or does not share the held root's native volume.");

            control = _files.OpenDirectoryChildNoFollow(heldRoot, RootMembershipRegistry.ControlDirectoryName);
            var controlInfo = _files.InspectHandle(control);
            if (controlInfo.Kind != PhysicalStoreEntryKind.Directory || controlInfo.Identity != controlEntry.Identity)
                throw Refused("The opened control directory changed from its no-follow observation.");

            var controlSemantics = _names.ObserveDirectoryNameSemantics(control);
            VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics, control, controlInfo.Identity, controlSemantics);

            var useId = Guid.NewGuid();
            var suffix = useId.ToString("N");
            var sentinelName = $"use-{suffix}.sentinel";
            var recordName = $"use-{suffix}.json";
            var stagedName = $"use-{suffix}.json.stage";

            cancellationToken.ThrowIfCancellationRequested();
            PhysicalStorePublicationChecks.RequireExpectedEntry(
                _files.InspectChildNoFollow(control, sentinelName), expectedIdentity: null);
            PhysicalStorePublicationChecks.RequireExpectedEntry(
                _files.InspectChildNoFollow(control, recordName), expectedIdentity: null);
            PhysicalStorePublicationChecks.RequireExpectedEntry(
                _files.InspectChildNoFollow(control, stagedName), expectedIdentity: null);
            sentinelFile = _files.CreateFileExclusiveAt(control, sentinelName);
            _files.WriteNewControlFile(sentinelFile, ReadOnlyMemory<byte>.Empty);
            var sentinelInfo = _files.InspectHandle(sentinelFile);
            RequireControlFile(sentinelInfo, expectedIdentity: null, expectedRoot.HandleIdentity, expectedLength: 0,
                "The new graph-use sentinel is not an empty regular single-link file on the held root's volume.");
            var sentinelIdentity = sentinelInfo.Identity;
            VerifyCanonicalFile(control, controlInfo.Identity, controlSemantics, sentinelName, sentinelIdentity, expectedLength: 0);
            VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics, control, controlInfo.Identity, controlSemantics);

            cancellationToken.ThrowIfCancellationRequested();
            sentinelLock = await _files.TryAcquireExclusiveLock(sentinelFile).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (sentinelLock is null)
                throw Refused("The newly created graph-use sentinel is already held by another process.");

            VerifyHeldSentinel(control, controlInfo.Identity, controlSemantics, sentinelFile, sentinelName, sentinelIdentity,
                expectedRoot.HandleIdentity);
            VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics, control, controlInfo.Identity, controlSemantics);

            var record = GraphUseRecord.Create(expectedRoot, enrollmentEpoch, useId, graphSnapshot, snapshotState,
                sentinelIdentity, GraphUseLifetimeKind.OsExclusiveSentinel, Environment.ProcessId);
            var payload = _serializer.Serialize(record);
            cancellationToken.ThrowIfCancellationRequested();

            PhysicalStorePublicationChecks.RequireExpectedEntry(
                _files.InspectChildNoFollow(control, recordName), expectedIdentity: null);
            PhysicalStorePublicationChecks.RequireExpectedEntry(
                _files.InspectChildNoFollow(control, stagedName), expectedIdentity: null);

            var stagedIdentity = CreateFlushedStage(control, stagedName, payload, expectedRoot.HandleIdentity);

            VerifyCanonicalFile(control, controlInfo.Identity, controlSemantics, stagedName, stagedIdentity, payload.Length);
            var stagedBytes = ReadExactFile(control, controlInfo.Identity, controlSemantics, stagedName, stagedIdentity,
                expectedRoot.HandleIdentity, GraphUsePayloadSerializer.MaximumPayloadBytes);
            RequireExactPayload(stagedBytes, payload, record);
            VerifyHeldSentinel(control, controlInfo.Identity, controlSemantics, sentinelFile, sentinelName, sentinelIdentity,
                expectedRoot.HandleIdentity);
            VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics, control, controlInfo.Identity, controlSemantics);
            cancellationToken.ThrowIfCancellationRequested();

            var prepared = PhysicalStorePublicationChecks.PreparePublish(_files, _names, control, stagedName,
                stagedIdentity, recordName, destinationIdentity: null);
            if (prepared.ParentIdentity != controlInfo.Identity || prepared.Semantics != controlSemantics)
                throw Refused("The staged payload does not bind the expected control-directory profile.");

            cancellationToken.ThrowIfCancellationRequested();
            _publication.PublishControlFileAt(control, stagedName, stagedIdentity, recordName, expectedDestinationIdentity: null);
            var published = PhysicalStorePublicationChecks.VerifyPublished(_files, _names, control, stagedName, recordName, prepared);
            if (published.Identity != stagedIdentity || published.Length != payload.Length)
                throw Refused("The published graph-use record does not match the staged identity and length.");

            var publishedBytes = ReadExactFile(control, controlInfo.Identity, controlSemantics, recordName, stagedIdentity,
                expectedRoot.HandleIdentity, GraphUsePayloadSerializer.MaximumPayloadBytes);
            RequireExactPayload(publishedBytes, payload, record);
            VerifyHeldSentinel(control, controlInfo.Identity, controlSemantics, sentinelFile, sentinelName, sentinelIdentity,
                expectedRoot.HandleIdentity);
            VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics, control, controlInfo.Identity, controlSemantics);
            cancellationToken.ThrowIfCancellationRequested();

            control.Dispose();
            control = null;
            var owner = new PublishedGraphUseRecord(record, recordName, stagedIdentity, sentinelName,
                sentinelIdentity, sentinelFile, sentinelLock);
            sentinelFile = null;
            sentinelLock = null;
            return owner;
        }
        catch (Exception primaryFailure)
        {
            var cleanupErrors = new List<Exception>();
            var sentinelCanClose = true;
            if (sentinelLock is not null)
            {
                try
                {
                    await sentinelLock.DisposeAsync().ConfigureAwait(false);
                    sentinelLock = null;
                }
                catch (Exception exception)
                {
                    cleanupErrors.Add(exception);
                    sentinelCanClose = false;
                }
            }

            if (sentinelFile is not null && sentinelCanClose)
            {
                try { sentinelFile.Dispose(); }
                catch (Exception exception) { cleanupErrors.Add(exception); }
            }

            if (control is not null)
            {
                try { control.Dispose(); }
                catch (Exception exception) { cleanupErrors.Add(exception); }
            }

            if (cleanupErrors.Count > 0)
            {
                var aggregate = new AggregateException(
                    "Graph-use publication failed and one or more owned resources could not be released with a confirmed outcome.",
                    new[] { primaryFailure }.Concat(cleanupErrors));
                if (sentinelLock is not null && sentinelFile is not null)
                    aggregate.Data["UnreleasedGraphUseSentinelOwnership"] = new UnreleasedGraphUseSentinelOwnership(sentinelFile, sentinelLock);
                throw aggregate;
            }

            throw;
        }
    }

    private PhysicalStoreNameSemantics VerifyRoot(PhysicalStoreDirectoryHandle root, PhysicalRootIdentity expectedRoot)
    {
        var info = _files.InspectHandle(root);
        if (info.Kind != PhysicalStoreEntryKind.Directory || info.Identity != expectedRoot.HandleIdentity)
            throw Refused("The supplied held directory does not match the expected physical root identity.");
        return _names.ObserveDirectoryNameSemantics(root);
    }

    private static void ValidateCandidate(
        PhysicalRootIdentity expectedRoot,
        long enrollmentEpoch,
        ProtectedGraphSnapshot graphSnapshot,
        PackageGraphUseSnapshotState snapshotState)
    {
        if (enrollmentEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(enrollmentEpoch));
        if (!Enum.IsDefined(snapshotState))
            throw new ArgumentOutOfRangeException(nameof(snapshotState));
        if (graphSnapshot.Disposition != ProtectedGraphDisposition.Active ||
            graphSnapshot.RecoverySelectionEvidence is not null || !graphSnapshot.Roots.Contains(expectedRoot))
            throw Refused("Publication requires an active-only graph candidate containing the exact held root.");

        foreach (var node in graphSnapshot.Nodes)
        {
            if (!SameVolume(node.Install.Root.HandleIdentity, node.Install.DirectoryIdentity))
                throw Refused("A graph install identity does not share its declared root's native provider and volume.");
        }
    }

    private void VerifyRootAndControl(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        PhysicalStoreNameSemantics expectedRootSemantics,
        PhysicalStoreDirectoryHandle control,
        PhysicalFileIdentity expectedControlIdentity,
        PhysicalStoreNameSemantics expectedControlSemantics)
    {
        var rootInfo = _files.InspectHandle(root);
        if (rootInfo.Kind != PhysicalStoreEntryKind.Directory || rootInfo.Identity != expectedRoot.HandleIdentity ||
            _names.ObserveDirectoryNameSemantics(root) != expectedRootSemantics)
            throw Refused("The held root identity or native name profile changed during graph-use publication.");

        var controlInfo = _files.InspectHandle(control);
        var namedControl = _files.InspectChildNoFollow(root, RootMembershipRegistry.ControlDirectoryName);
        var canonicalControl = _directoryNames.ObserveCanonicalDirectoryNameNoFollow(
            root, RootMembershipRegistry.ControlDirectoryName, expectedControlIdentity);
        if (controlInfo.Kind != PhysicalStoreEntryKind.Directory || controlInfo.Identity != expectedControlIdentity ||
            namedControl is null || namedControl.Kind != PhysicalStoreEntryKind.Directory || namedControl.Identity != expectedControlIdentity ||
            !SameVolume(expectedControlIdentity, expectedRoot.HandleIdentity) ||
            canonicalControl.ParentIdentity != expectedRoot.HandleIdentity || canonicalControl.FileIdentity != expectedControlIdentity ||
            canonicalControl.Semantics != expectedRootSemantics ||
            !string.Equals(canonicalControl.Basename, RootMembershipRegistry.ControlDirectoryName, StringComparison.Ordinal) ||
            _names.ObserveDirectoryNameSemantics(control) != expectedControlSemantics)
            throw Refused("The held control directory no longer binds the expected root and native name profile.");
    }

    private void VerifyHeldSentinel(
        PhysicalStoreDirectoryHandle control,
        PhysicalFileIdentity controlIdentity,
        PhysicalStoreNameSemantics controlSemantics,
        PhysicalStoreFileHandle sentinel,
        string sentinelName,
        PhysicalFileIdentity sentinelIdentity,
        PhysicalFileIdentity rootIdentity)
    {
        RequireControlFile(_files.InspectHandle(sentinel), sentinelIdentity, rootIdentity, expectedLength: 0,
            "The held graph-use sentinel changed after creation.");
        VerifyCanonicalFile(control, controlIdentity, controlSemantics, sentinelName, sentinelIdentity, expectedLength: 0);
    }

    private void VerifyCanonicalFile(
        PhysicalStoreDirectoryHandle parent,
        PhysicalFileIdentity expectedParentIdentity,
        PhysicalStoreNameSemantics expectedSemantics,
        string name,
        PhysicalFileIdentity expectedIdentity,
        long expectedLength)
    {
        var observed = PhysicalStorePublicationChecks.PrepareRemoval(_files, _names, parent, name, expectedIdentity);
        if (observed.ParentIdentity != expectedParentIdentity || observed.Semantics != expectedSemantics ||
            !string.Equals(observed.Basename, name, StringComparison.Ordinal))
            throw Refused("A graph-use transaction file is not bound to its exact canonical control-directory name.");

        var entry = PhysicalStorePublicationChecks.RequireExpectedEntry(
            _files.InspectChildNoFollow(parent, name), expectedIdentity)!;
        RequireControlFile(entry, expectedIdentity, expectedParentIdentity, expectedLength,
            "A graph-use transaction entry is not a regular single-link file of the expected length and volume.");
    }

    private byte[] ReadExactFile(
        PhysicalStoreDirectoryHandle parent,
        PhysicalFileIdentity parentIdentity,
        PhysicalStoreNameSemantics semantics,
        string name,
        PhysicalFileIdentity expectedIdentity,
        PhysicalFileIdentity rootIdentity,
        int maximumBytes)
    {
        var before = PhysicalStorePublicationChecks.RequireExpectedEntry(
            _files.InspectChildNoFollow(parent, name), expectedIdentity)!;
        RequireControlFile(before, expectedIdentity, rootIdentity, before.Length,
            "A graph-use payload entry changed before it was reopened.");
        var canonical = _names.ObserveCanonicalFileNameNoFollow(parent, name, expectedIdentity);
        if (canonical.ParentIdentity != parentIdentity || canonical.Semantics != semantics ||
            !string.Equals(canonical.Basename, name, StringComparison.Ordinal))
            throw Refused("A graph-use payload is not at its exact canonical control-directory name.");

        var opened = _files.OpenFileChildNoFollow(parent, name, FileAccess.Read);
        byte[] bytes;
        try
        {
            var info = _files.InspectHandle(opened);
            RequireControlFile(info, expectedIdentity, rootIdentity, before.Length,
                "The reopened graph-use payload changed identity, type, link count, volume, or length.");
            bytes = _files.ReadControlFile(opened, maximumBytes);
            var afterRead = _files.InspectHandle(opened);
            RequireControlFile(afterRead, expectedIdentity, rootIdentity, bytes.Length,
                "The graph-use payload changed while it was being read.");
            if (bytes.Length != before.Length)
                throw Refused("The graph-use payload length changed while it was being read.");
        }
        catch (Exception primaryFailure)
        {
            try { opened.Dispose(); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Reading a graph-use payload failed and its temporary handle could not be closed.",
                    primaryFailure, cleanupFailure);
            }
            throw;
        }
        opened.Dispose();

        VerifyCanonicalFile(parent, parentIdentity, semantics, name, expectedIdentity, bytes.Length);
        return bytes;
    }

    private PhysicalFileIdentity CreateFlushedStage(
        PhysicalStoreDirectoryHandle parent,
        string stagedName,
        byte[] payload,
        PhysicalFileIdentity rootIdentity)
    {
        var stagedFile = _files.CreateFileExclusiveAt(parent, stagedName);
        PhysicalFileIdentity stagedIdentity;
        try
        {
            _files.WriteNewControlFile(stagedFile, payload);
            var stagedInfo = _files.InspectHandle(stagedFile);
            RequireControlFile(stagedInfo, expectedIdentity: null, rootIdentity, payload.Length,
                "The new staged graph-use payload is not a regular single-link file on the held root's volume.");
            stagedIdentity = stagedInfo.Identity;
        }
        catch (Exception primaryFailure)
        {
            try { stagedFile.Dispose(); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Staging a graph-use payload failed and its creation handle could not be closed.",
                    primaryFailure, cleanupFailure);
            }
            throw;
        }

        stagedFile.Dispose();
        return stagedIdentity;
    }

    private void RequireExactPayload(byte[] actual, byte[] expected, GraphUseRecord expectedRecord)
    {
        if (!actual.AsSpan().SequenceEqual(expected))
            throw Refused("The reopened graph-use payload differs from the exact flushed candidate bytes.");

        var restored = _serializer.Deserialize(actual);
        if (!string.Equals(restored.PayloadDigest, expectedRecord.PayloadDigest, StringComparison.Ordinal) ||
            !restored.GraphSnapshot.HasSamePayloadAs(expectedRecord.GraphSnapshot) ||
            restored.RootIdentity != expectedRecord.RootIdentity ||
            restored.EnrollmentEpoch != expectedRecord.EnrollmentEpoch || restored.UseId != expectedRecord.UseId ||
            restored.SnapshotState != expectedRecord.SnapshotState || restored.SentinelIdentity != expectedRecord.SentinelIdentity ||
            restored.LifetimeKind != expectedRecord.LifetimeKind || restored.DiagnosticProcessId != expectedRecord.DiagnosticProcessId)
            throw Refused("The reopened graph-use record does not bind the exact candidate and sentinel identity.");
    }

    private static void RequireControlFile(
        PhysicalStoreEntryInfo info,
        PhysicalFileIdentity? expectedIdentity,
        PhysicalFileIdentity rootIdentity,
        long expectedLength,
        string message)
    {
        if (info.Kind != PhysicalStoreEntryKind.RegularFile || info.LinkCount != 1 ||
            (expectedIdentity is not null && info.Identity != expectedIdentity) ||
            info.Length != expectedLength || !SameVolume(info.Identity, rootIdentity))
            throw Refused(message);
    }

    private static bool SameVolume(PhysicalFileIdentity left, PhysicalFileIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.Ordinal) &&
           string.Equals(left.VolumeOrDeviceId, right.VolumeOrDeviceId, StringComparison.Ordinal);

    private static PackageStoreAdmissionException Refused(string message)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message);

    private sealed record UnreleasedGraphUseSentinelOwnership(
        PhysicalStoreFileHandle SentinelFile,
        IAsyncDisposable SentinelLock);
}

/// <summary>Retains only the native sentinel lifetime and descriptive publication metadata.</summary>
/// <remarks>It grants no package read, cleanup, or graph-completeness authority.</remarks>
internal sealed class PublishedGraphUseRecord : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly PhysicalStoreFileHandle _sentinelFile;
    private readonly IAsyncDisposable _sentinelLock;
    private Task? _disposeTask;

    internal PublishedGraphUseRecord(
        GraphUseRecord record,
        string recordName,
        PhysicalFileIdentity recordIdentity,
        string sentinelName,
        PhysicalFileIdentity sentinelIdentity,
        PhysicalStoreFileHandle sentinelFile,
        IAsyncDisposable sentinelLock)
    {
        Record = record;
        RecordName = recordName;
        RecordIdentity = recordIdentity;
        SentinelName = sentinelName;
        SentinelIdentity = sentinelIdentity;
        _sentinelFile = sentinelFile;
        _sentinelLock = sentinelLock;
    }

    internal GraphUseRecord Record { get; }
    internal string RecordName { get; }
    internal PhysicalFileIdentity RecordIdentity { get; }
    internal string SentinelName { get; }
    internal PhysicalFileIdentity SentinelIdentity { get; }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
            return new ValueTask(_disposeTask ??= ReleaseAsync());
    }

    private async Task ReleaseAsync()
    {
        await _sentinelLock.DisposeAsync().ConfigureAwait(false);
        _sentinelFile.Dispose();
    }
}
