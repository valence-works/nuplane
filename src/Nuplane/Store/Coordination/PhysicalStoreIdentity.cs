using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Separates stable parent/name identity from an atomically replaceable state file.</summary>
internal sealed class PhysicalStoreIdentity : IPhysicalStoreIdentity
{
    private readonly IPhysicalStoreFileSystem _fileSystem;
    private readonly IPhysicalStoreNameFileSystem _names;

    internal PhysicalStoreIdentity(IPhysicalStoreFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
        _names = fileSystem as IPhysicalStoreNameFileSystem ?? throw new PackageStoreAdmissionException(
            PackageStoreAdmissionReason.UnsupportedFilesystem,
            "The filesystem provider cannot establish native state-slot spelling and lookup semantics.");
    }

    /// <inheritdoc />
    public PhysicalStoreStateSlotObservation ObserveStateSlot(PhysicalStoreDirectoryHandle parent, string singleName)
    {
        ArgumentNullException.ThrowIfNull(parent);
        PhysicalStoreNames.ValidateSingleComponent(singleName);
        var parentBefore = _fileSystem.InspectHandle(parent);
        if (parentBefore.Kind != PhysicalStoreEntryKind.Directory)
            throw Unknown("The state parent is not a directory.");

        using var original = _fileSystem.OpenFileChildNoFollow(parent, singleName, FileAccess.Read);
        var before = _fileSystem.InspectHandle(original);
        RequireStateFile(before);
        var observed = _names.ObserveCanonicalFileNameNoFollow(parent, singleName, before.Identity);
        if (observed.ParentIdentity != parentBefore.Identity || observed.FileIdentity != before.Identity)
            throw Unknown("The native name observation does not bind the expected parent and state file.");

        using var canonical = _fileSystem.OpenFileChildNoFollow(parent, observed.Basename, FileAccess.Read);
        var canonicalInfo = _fileSystem.InspectHandle(canonical);
        var after = _fileSystem.InspectHandle(original);
        var parentAfter = _fileSystem.InspectHandle(parent);
        RequireStateFile(canonicalInfo);
        RequireStateFile(after);
        if (canonicalInfo.Identity != before.Identity || after.Identity != before.Identity ||
            parentAfter.Kind != PhysicalStoreEntryKind.Directory || parentAfter.Identity != parentBefore.Identity)
        {
            throw Unknown("The state slot changed during native name validation.");
        }

        return new PhysicalStoreStateSlotObservation(
            new StateSlotIdentity(parentBefore.Identity, observed.Semantics, observed.Basename),
            before.Identity);
    }

    private static void RequireStateFile(PhysicalStoreEntryInfo info)
    {
        if (info.Kind != PhysicalStoreEntryKind.RegularFile || info.LinkCount != 1)
            throw Unknown("A state slot requires one regular file without hard-link ambiguity.");
    }

    private static PackageStoreAdmissionException Unknown(string message)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message);
}
