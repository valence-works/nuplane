using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Shares exact-directory and canonical-name checks across native no-replace publishers.</summary>
internal static class PhysicalStoreDirectoryPublicationChecks
{
    internal static PhysicalStoreCanonicalName PreparePublish(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreDirectoryPublicationFileSystem directories,
        PhysicalStoreDirectoryHandle parent,
        string stagedName,
        PhysicalFileIdentity stagedIdentity,
        string destinationName)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(stagedIdentity);
        PhysicalStoreNames.ValidateSingleComponent(stagedName);
        PhysicalStoreNames.ValidateSingleComponent(destinationName);
        if (string.Equals(stagedName, destinationName, StringComparison.Ordinal))
            throw Unknown("Directory publication requires distinct source and destination entries.");

        var staged = ObserveExactDirectory(files, directories, parent, stagedName, stagedIdentity);
        PhysicalStoreCanonicalName.ValidateBasename(destinationName, staged.Semantics);
        PhysicalStorePublicationChecks.RequireExpectedEntry(files.InspectChildNoFollow(parent, destinationName), null);
        return staged;
    }

    internal static PhysicalStoreEntryInfo VerifyPublished(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreDirectoryPublicationFileSystem directories,
        PhysicalStoreDirectoryHandle parent,
        string stagedName,
        string destinationName,
        PhysicalStoreCanonicalName prepared)
    {
        var published = ObserveExactDirectory(files, directories, parent, destinationName, prepared.FileIdentity);
        if (published.ParentIdentity != prepared.ParentIdentity || published.Semantics != prepared.Semantics)
            throw Unknown("Directory publication changed the held parent or native name profile.");
        if (files.InspectChildNoFollow(parent, stagedName) is not null)
            throw Unknown("The staged directory name remains present after publication.");
        PhysicalStorePublicationChecks.RequireParent(files, parent, prepared.ParentIdentity);
        return RequireExpectedDirectory(files.InspectChildNoFollow(parent, destinationName), prepared.FileIdentity);
    }

    internal static PhysicalStoreEntryInfo RequireExpectedDirectory(
        PhysicalStoreEntryInfo? entry, PhysicalFileIdentity expectedIdentity)
    {
        if (entry is null || entry.Kind != PhysicalStoreEntryKind.Directory || entry.Identity != expectedIdentity)
            throw Unknown("The directory entry is absent, linked, not a directory, or has a different identity.");
        return entry;
    }

    private static PhysicalStoreCanonicalName ObserveExactDirectory(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreDirectoryPublicationFileSystem directories,
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedIdentity)
    {
        RequireExpectedDirectory(files.InspectChildNoFollow(parent, singleName), expectedIdentity);
        var observed = directories.ObserveCanonicalDirectoryNameNoFollow(parent, singleName, expectedIdentity);
        if (!string.Equals(observed.Basename, singleName, StringComparison.Ordinal))
            throw Unknown("Directory publication requires exact native canonical spelling, not an alias.");
        PhysicalStorePublicationChecks.RequireParent(files, parent, observed.ParentIdentity);
        return observed;
    }

    private static PackageStoreAdmissionException Unknown(string message)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message);
}
