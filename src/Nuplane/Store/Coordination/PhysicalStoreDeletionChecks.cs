using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Shares exact-entry checks for native file and empty-directory removal.</summary>
internal static class PhysicalStoreDeletionChecks
{
    internal static PhysicalStoreCanonicalName PrepareDirectoryRemoval(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreDirectoryNameFileSystem directories,
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedIdentity)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        PhysicalStoreNames.ValidateSingleComponent(singleName);
        var observed = PhysicalStoreDirectoryPublicationChecks.ObserveExactDirectory(
            files, directories, parent, singleName, expectedIdentity);
        RequireSameVolume(observed.ParentIdentity, expectedIdentity);
        return observed;
    }

    internal static void RevalidateDirectoryRemoval(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreDirectoryNameFileSystem directories,
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalStoreCanonicalName prepared)
    {
        var current = PrepareDirectoryRemoval(files, directories, parent, singleName, prepared.FileIdentity);
        if (current != prepared)
            throw Unknown("The exact directory or its parent/name profile changed before removal.");
    }

    internal static void RequireSameVolume(PhysicalFileIdentity parent, PhysicalFileIdentity entry)
    {
        if (!string.Equals(parent.Provider, entry.Provider, StringComparison.Ordinal) ||
            !string.Equals(parent.VolumeOrDeviceId, entry.VolumeOrDeviceId, StringComparison.Ordinal))
            throw Unknown("Entry removal cannot cross the held parent's native provider or volume.");
    }

    internal static void VerifyRemoved(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalStoreCanonicalName prepared)
    {
        PhysicalStorePublicationChecks.RequireParent(files, parent, prepared.ParentIdentity);
        if (names.ObserveDirectoryNameSemantics(parent) != prepared.Semantics)
            throw Unknown("The held parent's native name profile changed during entry removal.");
        PhysicalStorePublicationChecks.VerifyRemoved(files, parent, singleName, prepared);
    }

    private static PackageStoreAdmissionException Unknown(string message)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message);
}
