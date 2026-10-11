using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Owns one replayed member-state locator's held parent and native slot evidence.</summary>
/// <remarks>
/// This is metadata only. The parent remains usable only during its root-and-member-lock callback, and the
/// result grants no package access, deletion authority, or membership completeness.
/// </remarks>
internal sealed class ResolvedMemberStateLocation : IDisposable
{
    private readonly IPhysicalStoreFileSystem _files;
    private readonly IPhysicalStoreNameFileSystem _names;
    private readonly RootMembershipRegistry.MemberLocatorReplayScope _scope;
    private readonly ResolvedPackageStorePath _parentResolution;
    private bool _disposed;

    internal ResolvedMemberStateLocation(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        RootMembershipRegistry.MemberLocatorReplayScope scope,
        ResolvedPackageStorePath parentResolution,
        string requestedBasename,
        StateSlotIdentity slot,
        PhysicalFileIdentity? existingFileIdentity)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(parentResolution);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedBasename);
        ArgumentNullException.ThrowIfNull(slot);

        _files = files;
        _names = names;
        _scope = scope;
        _parentResolution = parentResolution;
        RequestedBasename = requestedBasename;
        Slot = slot;
        ExistingFileIdentity = existingFileIdentity;
    }

    /// <summary>Gets the independently replayed basename from the persisted absolute locator.</summary>
    internal string RequestedBasename { get; }

    /// <summary>Gets the native canonical slot spelling and parent identity.</summary>
    internal StateSlotIdentity Slot { get; }

    /// <summary>Gets the current state file identity, or null after a positive observation of absence.</summary>
    internal PhysicalFileIdentity? ExistingFileIdentity { get; }

    /// <summary>Gets the held parent while the enclosing registry callback remains active.</summary>
    internal PhysicalStoreDirectoryHandle Parent
    {
        get
        {
            EnsureActive();
            return _parentResolution.Target as PhysicalStoreDirectoryHandle
                ?? throw Refused("The configured member locator did not resolve to a directory parent.");
        }
    }

    /// <summary>Revalidates the retained path evidence and final state slot without reading its payload.</summary>
    internal void Revalidate()
    {
        EnsureActive();
        _parentResolution.Revalidate();

        var parent = Parent;
        var before = _files.InspectHandle(parent);
        if (before.Kind != PhysicalStoreEntryKind.Directory || before.Identity != Slot.ParentIdentity)
            throw Refused("The configured member state parent changed after locator replay.");

        if (ExistingFileIdentity is { } expectedFileIdentity)
        {
            var observed = new PhysicalStoreIdentity(_files).ObserveStateSlot(parent, RequestedBasename);
            if (observed.Slot != Slot || observed.FileIdentity != expectedFileIdentity)
                throw Refused("The configured member state slot changed after locator replay.");
        }
        else
        {
            var semantics = _names.ObserveDirectoryNameSemantics(parent);
            var afterProfile = _files.InspectHandle(parent);
            if (afterProfile.Kind != PhysicalStoreEntryKind.Directory || afterProfile.Identity != before.Identity ||
                semantics != Slot.NameSemantics)
            {
                throw Refused("The configured member parent profile changed after prospective-slot replay.");
            }

            RequireAbsent(parent);
            PhysicalStorePublicationChecks.RequireParent(_files, parent, Slot.ParentIdentity);
            RequireAbsent(parent);
        }

        _scope.EnsureActive();
        _parentResolution.Revalidate();
    }

    /// <summary>Closes the held parent and expires this metadata result.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _parentResolution.Dispose();
    }

    private void RequireAbsent(PhysicalStoreDirectoryHandle parent)
    {
        if (_files.InspectChildNoFollow(parent, RequestedBasename) is not null)
            throw Refused("A prospective member state slot is no longer absent.");
    }

    private void EnsureActive()
    {
        _scope.EnsureActive();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static PackageStoreAdmissionException Refused(string message)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message);
}
