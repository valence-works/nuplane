using System.Runtime.ExceptionServices;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Owns native parent evidence for one replaceable state-file name.</summary>
/// <remarks>
/// This metadata-only result retains both the path evidence used during parent creation and the strict final
/// re-resolution. Revalidation checks the retained parent and authority, never the replaceable final file identity.
/// It grants no root/member lock or persistence authority.
/// </remarks>
internal sealed class ResolvedNativeStateSlot : IDisposable
{
    private readonly IPhysicalStoreFileSystem _files;
    private readonly IPhysicalStoreNameFileSystem _names;
    private readonly ResolvedPackageStorePath _creationEvidence;
    private readonly ResolvedPackageStorePath _finalEvidence;
    private readonly object _gate = new();
    private bool _disposed;

    internal ResolvedNativeStateSlot(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        ResolvedPackageStorePath creationEvidence,
        ResolvedPackageStorePath finalEvidence,
        string requestedBasename,
        StateSlotIdentity slot,
        PhysicalFileIdentity? existingFileIdentity)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(creationEvidence);
        ArgumentNullException.ThrowIfNull(finalEvidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedBasename);
        ArgumentNullException.ThrowIfNull(slot);

        _files = files;
        _names = names;
        _creationEvidence = creationEvidence;
        _finalEvidence = finalEvidence;
        RequestedBasename = requestedBasename;
        Slot = slot;
        ExistingFileIdentity = existingFileIdentity;
    }

    /// <summary>Gets the native canonical state-slot identity.</summary>
    internal StateSlotIdentity Slot { get; }

    /// <summary>Gets the requested spelling of the replaceable final state-file name.</summary>
    internal string RequestedBasename { get; }

    /// <summary>Gets the final file identity observed without opening or reading payload bytes, when present.</summary>
    internal PhysicalFileIdentity? ExistingFileIdentity { get; }

    /// <summary>Gets the held parent directory while this result remains active.</summary>
    internal PhysicalStoreDirectoryHandle Parent
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _finalEvidence.Target as PhysicalStoreDirectoryHandle
                    ?? throw Refused("The native state-slot resolution has no held parent directory.");
            }
        }
    }

    /// <summary>Gets the retained root authority, when the path encountered one.</summary>
    internal PhysicalRootIdentity? RootIdentity => _finalEvidence.RootIdentity;

    /// <summary>Gets the retained membership candidate, when the path encountered one.</summary>
    internal RootMembershipRecord? MembershipCandidate => _finalEvidence.MembershipCandidate;

    /// <summary>Gets the retained membership ledger file identity, when the path encountered one.</summary>
    internal PhysicalFileIdentity? MembershipLedgerIdentity => _finalEvidence.MembershipLedgerIdentity;

    internal static void RequireSameAuthorityEvidence(
        ResolvedPackageStorePath original,
        ResolvedPackageStorePath final)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(final);
        if (original.RootIdentity != final.RootIdentity ||
            original.MembershipLedgerIdentity != final.MembershipLedgerIdentity ||
            original.MembershipCandidate?.RootIdentity != final.MembershipCandidate?.RootIdentity ||
            original.MembershipCandidate?.LedgerDigest != final.MembershipCandidate?.LedgerDigest ||
            original.MembershipCandidate?.EnrollmentEpoch != final.MembershipCandidate?.EnrollmentEpoch ||
            original.MembershipCandidate?.Status != final.MembershipCandidate?.Status ||
            original.MembershipCandidate?.PendingStateCommit != final.MembershipCandidate?.PendingStateCommit)
        {
            throw Refused("The state parent authority changed during strict post-creation re-resolution.",
                final.RootIdentity ?? original.RootIdentity);
        }
    }

    /// <summary>Replays both original and final path evidence and confirms their held target is the same parent.</summary>
    /// <remarks>The replay does not pin or require the replaceable final state-file entry to retain its identity.</remarks>
    internal void RevalidateRetainedParent(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            _creationEvidence.Revalidate();
            cancellationToken.ThrowIfCancellationRequested();
            _finalEvidence.Revalidate();
            cancellationToken.ThrowIfCancellationRequested();

            var originalParent = RequireParent(_creationEvidence);
            var finalParent = RequireParent(_finalEvidence);
            var original = _files.InspectHandle(originalParent);
            var originalSemantics = _names.ObserveDirectoryNameSemantics(originalParent);
            var current = _files.InspectHandle(finalParent);
            var currentSemantics = _names.ObserveDirectoryNameSemantics(finalParent);
            if (original.Kind != PhysicalStoreEntryKind.Directory || current.Kind != PhysicalStoreEntryKind.Directory ||
                original.Identity != Slot.ParentIdentity || current.Identity != Slot.ParentIdentity ||
                original.Identity != current.Identity || originalSemantics != Slot.NameSemantics ||
                currentSemantics != Slot.NameSemantics)
            {
                throw Refused("The retained native state-slot parent changed after resolution.");
            }

            RequireSameAuthorityEvidence(_creationEvidence, _finalEvidence);
        }
    }

    /// <summary>Releases both retained path resolutions, attempting each owner in reverse acquisition order.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        List<Exception>? errors = null;
        try { _finalEvidence.Dispose(); }
        catch (Exception exception) { (errors ??= []).Add(exception); }
        try { _creationEvidence.Dispose(); }
        catch (Exception exception) { (errors ??= []).Add(exception); }

        if (errors is { Count: 1 })
            ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors is { Count: > 1 })
            throw new AggregateException("Native state-slot path evidence could not be fully released.", errors);
    }

    private static PhysicalStoreDirectoryHandle RequireParent(ResolvedPackageStorePath resolution)
        => resolution.Target as PhysicalStoreDirectoryHandle
           ?? throw Refused("A retained native state-slot target is not a directory parent.");

    private static PackageStoreAdmissionException Refused(string message, PhysicalRootIdentity? root = null)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message, root);
}
