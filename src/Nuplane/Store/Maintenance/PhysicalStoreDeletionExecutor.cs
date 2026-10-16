using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.DeletionRecords;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Maintenance;

/// <summary>Walks one fresh journal-bound quarantine through the internal marker-last deletion protocol.</summary>
internal sealed class PhysicalStoreDeletionExecutor
{
    private readonly IPhysicalStoreFileSystem _files;
    private readonly IPhysicalStoreNameFileSystem _names;
    private readonly IPhysicalStoreDirectoryNameFileSystem _directories;
    private readonly IPhysicalStoreDeletionFileSystem _deletion;
    private readonly Action<PackageStoreDeletionExecutionPoint, PackageStoreDeletionJournal>? _checkpoint;

    internal PhysicalStoreDeletionExecutor(
        IPhysicalStoreFileSystem files,
        Action<PackageStoreDeletionExecutionPoint, PackageStoreDeletionJournal>? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files = files;
        _names = files as IPhysicalStoreNameFileSystem
            ?? throw Unsupported("Recursive deletion requires exact native name observations.");
        _directories = files as IPhysicalStoreDirectoryNameFileSystem
            ?? throw Unsupported("Recursive deletion requires exact native directory-name observations.");
        _deletion = files as IPhysicalStoreDeletionFileSystem
            ?? throw Unsupported("Recursive deletion requires native exact-entry removal.");
        _checkpoint = checkpoint;
    }

    internal void Execute(
        PackageStoreQuarantinedCandidate candidate,
        Action<PackageStoreQuarantinedCandidate, CancellationToken> publishDeleting,
        Action<PackageStoreQuarantinedCandidate, CancellationToken> publishFinalizing,
        Action<CancellationToken> revalidateEligibility,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(publishDeleting);
        ArgumentNullException.ThrowIfNull(publishFinalizing);
        ArgumentNullException.ThrowIfNull(revalidateEligibility);

        var phase = candidate.Journal.Phase;
        if (phase == PackageStoreDeletionPhase.Quarantined)
        {
            RequireMarkerAndFullBaseline(candidate.ObserveCurrentSubset(cancellationToken));
            revalidateEligibility(cancellationToken);
            candidate.ReleaseMarkerObservation(cancellationToken);
            publishDeleting(candidate, cancellationToken);
            _checkpoint?.Invoke(PackageStoreDeletionExecutionPoint.AfterDeletingPublished, candidate.Journal);
            phase = candidate.Journal.Phase;
        }

        if (phase == PackageStoreDeletionPhase.Deleting)
        {
            candidate.ReleaseMarkerObservation(cancellationToken);
            DeletePayload(candidate, revalidateEligibility, cancellationToken);
            var beforeFinalizing = candidate.ObserveCurrentSubset(cancellationToken);
            RequireMarkerOnly(beforeFinalizing);
            revalidateEligibility(cancellationToken);
            publishFinalizing(candidate, cancellationToken);
            _checkpoint?.Invoke(PackageStoreDeletionExecutionPoint.AfterFinalizingPublished, candidate.Journal);
            phase = candidate.Journal.Phase;
        }

        if (phase != PackageStoreDeletionPhase.Finalizing)
            throw Refuse(candidate, "The exact quarantine journal is not at a supported deletion phase.");

        FinishFinalizing(candidate, revalidateEligibility, cancellationToken);
    }

    private void DeletePayload(
        PackageStoreQuarantinedCandidate candidate,
        Action<CancellationToken> revalidateEligibility,
        CancellationToken cancellationToken)
    {
        var baseline = candidate.Manifest.Entries
            .Where(static entry => !(entry.RelativeComponents.Length == 1 &&
                string.Equals(entry.RelativeComponents[0], PackageStoreDeletionJournal.CompletionMarkerName,
                    StringComparison.Ordinal)))
            .ToArray();
        var files = baseline
            .Where(static entry => entry.Kind == PhysicalStoreEntryKind.RegularFile)
            .OrderBy(static entry => PhysicalStoreDeletionManifest.GetPathKey(entry.RelativeComponents), StringComparer.Ordinal);
        var directories = baseline
            .Where(static entry => entry.Kind == PhysicalStoreEntryKind.Directory)
            .OrderByDescending(static entry => entry.RelativeComponents.Length)
            .ThenBy(static entry => PhysicalStoreDeletionManifest.GetPathKey(entry.RelativeComponents), StringComparer.Ordinal);

        foreach (var entry in files.Concat(directories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observed = candidate.ObserveCurrentSubset(cancellationToken);
            var key = PhysicalStoreDeletionManifest.GetPathKey(entry.RelativeComponents);
            var current = observed.Manifest.Entries.SingleOrDefault(item =>
                string.Equals(PhysicalStoreDeletionManifest.GetPathKey(item.RelativeComponents), key,
                    StringComparison.Ordinal));
            if (current is null)
                continue;

            using var parent = OpenParent(candidate, entry, cancellationToken);
            RequireMutationAuthority(candidate, revalidateEligibility, cancellationToken);
            RequireExactEntry(parent.Handle, parent.Identity, parent.Semantics, entry, candidate);
            if (entry.Kind == PhysicalStoreEntryKind.RegularFile)
                _deletion.RemoveFileAt(parent.Handle, entry.RelativeComponents[^1], entry.Identity);
            else
                _deletion.RemoveEmptyDirectoryAt(parent.Handle, entry.RelativeComponents[^1], entry.Identity);
            _checkpoint?.Invoke(PackageStoreDeletionExecutionPoint.AfterPayloadEntryRemoved, candidate.Journal);

            var after = candidate.ObserveCurrentSubset(cancellationToken);
            if (after.Manifest.Entries.Any(item => string.Equals(
                    PhysicalStoreDeletionManifest.GetPathKey(item.RelativeComponents), key, StringComparison.Ordinal)))
                throw Refuse(candidate, "A native deletion primitive returned while the exact manifest entry remained present.");
        }
    }

    private void FinishFinalizing(
        PackageStoreQuarantinedCandidate candidate,
        Action<CancellationToken> revalidateEligibility,
        CancellationToken cancellationToken)
    {
        var current = candidate.ObserveCurrentSubset(cancellationToken);
        if (!current.Manifest.Entries.IsEmpty)
        {
            RequireMarkerOnly(current);
            candidate.ReleaseMarkerObservation(cancellationToken);
            RequireMutationAuthority(candidate, revalidateEligibility, cancellationToken);
            _deletion.RemoveFileAt(candidate.QuarantineHandle,
                PackageStoreDeletionJournal.CompletionMarkerName, candidate.Journal.CompletionMarkerIdentity);
            _checkpoint?.Invoke(PackageStoreDeletionExecutionPoint.AfterMarkerRemoved, candidate.Journal);
        }

        var empty = candidate.ObserveCurrentSubset(cancellationToken);
        if (!empty.Manifest.Entries.IsEmpty)
            throw Refuse(candidate, "The Finalizing quarantine was not positively empty after marker removal.");

        RequireMutationAuthority(candidate, revalidateEligibility, cancellationToken);
        candidate.CloseQuarantineForRootRemoval(cancellationToken);
        _checkpoint?.Invoke(PackageStoreDeletionExecutionPoint.AfterQuarantineHandleClosed, candidate.Journal);
        revalidateEligibility(cancellationToken);
        if (candidate.InspectOriginalSlot(cancellationToken) is not null)
            throw Refuse(candidate, "The original logical install slot became occupied before quarantine-root removal.");
        candidate.MarkQuarantineRootRemovalAttempted();
        _deletion.RemoveEmptyDirectoryAt(candidate.PackageParentHandle,
            candidate.Journal.QuarantineBasename, candidate.Journal.Manifest.InstallDirectoryIdentity);
        candidate.MarkQuarantineRootRemovalReturned();
        _checkpoint?.Invoke(PackageStoreDeletionExecutionPoint.AfterQuarantineRootRemoved, candidate.Journal);
        candidate.VerifyQuarantineRemoved(cancellationToken);
    }

    private static void RequireMutationAuthority(
        PackageStoreQuarantinedCandidate candidate,
        Action<CancellationToken> revalidateEligibility,
        CancellationToken cancellationToken)
    {
        revalidateEligibility(cancellationToken);
        candidate.Revalidate(cancellationToken);
        if (candidate.InspectOriginalSlot(cancellationToken) is not null)
            throw Refuse(candidate, "The original logical install slot must remain positively absent before every removal.");
    }

    private ParentObservation OpenParent(
        PackageStoreQuarantinedCandidate candidate,
        PhysicalStoreDeletionManifestEntry entry,
        CancellationToken cancellationToken)
    {
        var components = entry.RelativeComponents;
        var handles = new List<PhysicalStoreDirectoryHandle>();
        var parent = candidate.QuarantineHandle;
        var parentIdentity = candidate.Manifest.InstallDirectoryIdentity;
        var parentSemantics = candidate.Manifest.InstallDirectorySemantics;
        try
        {
            for (var index = 0; index < components.Length - 1; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var prefix = components.Take(index + 1).ToArray();
                var expected = candidate.Manifest.Entries.Single(row =>
                    row.RelativeComponents.SequenceEqual(prefix, StringComparer.Ordinal));
                if (expected.Kind != PhysicalStoreEntryKind.Directory)
                    throw Refuse(candidate, "A manifest parent row is not an exact directory.");
                RequireExactDirectory(parent, parentIdentity, parentSemantics,
                    components[index], expected, candidate);
                var child = _files.OpenDirectoryChildNoFollow(parent, components[index]);
                handles.Add(child);
                if (_files.InspectHandle(child) is not { Kind: PhysicalStoreEntryKind.Directory } info ||
                    info.Identity != expected.Identity || info.ReparseTag is not (null or 0) ||
                    _names.ObserveDirectoryNameSemantics(child) != expected.DirectorySemantics)
                    throw Refuse(candidate, "A manifest parent changed during native directory open.");
                parent = child;
                parentIdentity = expected.Identity;
                parentSemantics = expected.DirectorySemantics!;
            }

            return new ParentObservation(parent, parentIdentity, parentSemantics, handles);
        }
        catch (Exception primaryFailure)
        {
            try { DisposeReverse(handles); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Opening the exact deletion parent failed and native handles also failed to close.",
                    primaryFailure, cleanupFailure);
            }
            throw;
        }
    }

    private void RequireExactEntry(
        PhysicalStoreDirectoryHandle parent,
        PhysicalFileIdentity parentIdentity,
        PhysicalStoreNameSemantics parentSemantics,
        PhysicalStoreDeletionManifestEntry entry,
        PackageStoreQuarantinedCandidate candidate)
    {
        var name = entry.RelativeComponents[^1];
        PhysicalStoreDeletionManifestEntry.ValidateComponent(name, parentSemantics);
        var info = _files.InspectChildNoFollow(parent, name);
        if (info is null || info.Kind != entry.Kind || info.Identity != entry.Identity ||
            info.ReparseTag is not (null or 0) ||
            !string.Equals(info.Identity.Provider, candidate.Journal.StoreRoot.HandleIdentity.Provider, StringComparison.Ordinal) ||
            !string.Equals(info.Identity.VolumeOrDeviceId, candidate.Journal.StoreRoot.HandleIdentity.VolumeOrDeviceId, StringComparison.Ordinal))
            throw Refuse(candidate, "A deletion target no longer has its exact no-follow identity and kind.");

        if (entry.Kind == PhysicalStoreEntryKind.RegularFile)
        {
            if (info.LinkCount != 1)
                throw Refuse(candidate, "A deletion target file is no longer single-link.");
            var canonical = _names.ObserveCanonicalFileNameNoFollow(parent, name, entry.Identity);
            if (canonical.ParentIdentity != parentIdentity || canonical.Semantics != parentSemantics ||
                canonical.FileIdentity != entry.Identity || !string.Equals(canonical.Basename, name, StringComparison.Ordinal))
                throw Refuse(candidate, "A deletion target file no longer has its exact canonical name and parent profile.");
        }
        else
        {
            var canonical = _directories.ObserveCanonicalDirectoryNameNoFollow(parent, name, entry.Identity);
            if (canonical.ParentIdentity != parentIdentity || canonical.Semantics != parentSemantics ||
                canonical.FileIdentity != entry.Identity || !string.Equals(canonical.Basename, name, StringComparison.Ordinal))
                throw Refuse(candidate, "A deletion target directory no longer has its exact canonical name and parent profile.");
            using var child = _files.OpenDirectoryChildNoFollow(parent, name);
            if (_files.InspectHandle(child)?.Identity != entry.Identity ||
                _names.ObserveDirectoryNameSemantics(child) != entry.DirectorySemantics)
                throw Refuse(candidate, "A deletion target directory profile changed before removal.");
        }
    }

    private void RequireExactDirectory(
        PhysicalStoreDirectoryHandle parent,
        PhysicalFileIdentity parentIdentity,
        PhysicalStoreNameSemantics parentSemantics,
        string name,
        PhysicalStoreDeletionManifestEntry entry,
        PackageStoreQuarantinedCandidate candidate)
    {
        PhysicalStoreDeletionManifestEntry.ValidateComponent(name, parentSemantics);
        var info = _files.InspectChildNoFollow(parent, name);
        var canonical = _directories.ObserveCanonicalDirectoryNameNoFollow(parent, name, entry.Identity);
        if (info is null || info.Kind != PhysicalStoreEntryKind.Directory || info.Identity != entry.Identity ||
            info.ReparseTag is not (null or 0) || canonical.ParentIdentity != parentIdentity ||
            canonical.Semantics != parentSemantics || canonical.FileIdentity != entry.Identity ||
            !string.Equals(canonical.Basename, name, StringComparison.Ordinal))
            throw Refuse(candidate, "A deletion target parent no longer has its exact canonical identity and profile.");
    }

    private static void RequireMarkerAndFullBaseline(PhysicalStoreDeletionPartialObservation observation)
    {
        if (!observation.PositivelyAbsentEntries.IsEmpty || observation.Manifest.Entries.Length == 0 ||
            !observation.Manifest.Entries.Any(static entry => entry.RelativeComponents.Length == 1 &&
                string.Equals(entry.RelativeComponents[0], PackageStoreDeletionJournal.CompletionMarkerName,
                    StringComparison.Ordinal)))
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                "Quarantined phase requires the exact full baseline and completion marker.");
    }

    private static void RequireMarkerOnly(PhysicalStoreDeletionPartialObservation observation)
    {
        if (observation.Manifest.Entries.Length != 1 ||
            observation.Manifest.Entries[0].RelativeComponents.Length != 1 ||
            !string.Equals(observation.Manifest.Entries[0].RelativeComponents[0],
                PackageStoreDeletionJournal.CompletionMarkerName, StringComparison.Ordinal))
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                "Finalizing requires a complete native observation of the exact marker as the sole child.");
    }

    private static void DisposeReverse(IEnumerable<PhysicalStoreDirectoryHandle> handles)
    {
        List<Exception>? failures = null;
        foreach (var handle in handles.Reverse())
        {
            try { handle.Dispose(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is { Count: > 1 })
            throw new AggregateException("Multiple native deletion parent handles failed to close.", failures);
    }

    private static PackageStoreAdmissionException Refuse(PackageStoreQuarantinedCandidate candidate, string message)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message, candidate.Journal.StoreRoot);

    private static PackageStoreAdmissionException Unsupported(string message)
        => new(PackageStoreAdmissionReason.UnsupportedFilesystem, message);

    private sealed class ParentObservation(
        PhysicalStoreDirectoryHandle handle,
        PhysicalFileIdentity identity,
        PhysicalStoreNameSemantics semantics,
        IReadOnlyList<PhysicalStoreDirectoryHandle> ownedHandles) : IDisposable
    {
        internal PhysicalStoreDirectoryHandle Handle { get; } = handle;
        internal PhysicalFileIdentity Identity { get; } = identity;
        internal PhysicalStoreNameSemantics Semantics { get; } = semantics;

        public void Dispose() => DisposeReverse(ownedHandles);
    }
}

internal enum PackageStoreDeletionExecutionPoint
{
    AfterQuarantinedPublished,
    AfterDeletingPublished,
    AfterPayloadEntryRemoved,
    AfterFinalizingPublished,
    AfterMarkerRemoved,
    AfterQuarantineHandleClosed,
    AfterQuarantineRootRemoved
}
