using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Shares canonical single-file publication checks across native providers.</summary>
internal static class PhysicalStorePublicationChecks
{
    internal static PhysicalStoreCanonicalName PreparePublish(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        PhysicalStoreDirectoryHandle parent,
        string stagedName,
        PhysicalFileIdentity stagedIdentity,
        string destinationName,
        PhysicalFileIdentity? destinationIdentity)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(stagedIdentity);
        PhysicalStoreNames.ValidateSingleComponent(stagedName);
        PhysicalStoreNames.ValidateSingleComponent(destinationName);
        if (string.Equals(stagedName, destinationName, StringComparison.Ordinal) || stagedIdentity == destinationIdentity)
            throw Unknown("Publication requires distinct staged and destination entries.");

        var staged = ObserveExactFile(files, names, parent, stagedName, stagedIdentity);
        RequireExpectedEntry(files.InspectChildNoFollow(parent, destinationName), destinationIdentity);
        if (destinationIdentity is not null)
        {
            var destination = ObserveExactFile(files, names, parent, destinationName, destinationIdentity);
            if (destination.ParentIdentity != staged.ParentIdentity || destination.Semantics != staged.Semantics)
                throw Unknown("Publication entries do not share one parent and native name profile.");
        }

        return staged;
    }

    internal static PhysicalStoreEntryInfo VerifyPublished(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        PhysicalStoreDirectoryHandle parent,
        string stagedName,
        string destinationName,
        PhysicalStoreCanonicalName prepared)
    {
        var published = ObserveExactFile(files, names, parent, destinationName, prepared.FileIdentity);
        if (published.ParentIdentity != prepared.ParentIdentity || published.Semantics != prepared.Semantics)
            throw Unknown("Publication changed the held parent or native name profile.");
        if (files.InspectChildNoFollow(parent, stagedName) is not null)
            throw Unknown("The staged entry still exists after publication.");
        RequireParent(files, parent, prepared.ParentIdentity);
        return RequireExpectedEntry(files.InspectChildNoFollow(parent, destinationName), prepared.FileIdentity)!;
    }

    internal static PhysicalStoreCanonicalName PrepareRemoval(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedIdentity)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        PhysicalStoreNames.ValidateSingleComponent(singleName);
        return ObserveExactFile(files, names, parent, singleName, expectedIdentity);
    }

    internal static void VerifyRemoved(
        IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalStoreCanonicalName prepared)
    {
        RequireParent(files, parent, prepared.ParentIdentity);
        if (files.InspectChildNoFollow(parent, singleName) is not null)
            throw Unknown("The transaction-control entry remains present after removal.");
    }

    internal static PhysicalStoreEntryInfo? RequireExpectedEntry(
        PhysicalStoreEntryInfo? entry,
        PhysicalFileIdentity? expectedIdentity)
    {
        if (expectedIdentity is null)
        {
            if (entry is not null)
                throw Unknown("Publication requires a positively absent destination.");
            return null;
        }

        if (entry is null || entry.Kind != PhysicalStoreEntryKind.RegularFile || entry.LinkCount != 1 ||
            entry.Identity != expectedIdentity)
        {
            throw Unknown("The control entry is absent, linked, not regular, or has a different identity.");
        }

        return entry;
    }

    internal static void RequireParent(
        IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle parent,
        PhysicalFileIdentity expectedIdentity)
    {
        var actual = files.InspectHandle(parent);
        if (actual.Kind != PhysicalStoreEntryKind.Directory || actual.Identity != expectedIdentity)
            throw Unknown("The held publication parent identity changed.");
    }

    private static PhysicalStoreCanonicalName ObserveExactFile(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedIdentity)
    {
        RequireExpectedEntry(files.InspectChildNoFollow(parent, singleName), expectedIdentity);
        var observed = names.ObserveCanonicalFileNameNoFollow(parent, singleName, expectedIdentity);
        if (!string.Equals(observed.Basename, singleName, StringComparison.Ordinal))
            throw Unknown("Publication requires the exact native canonical basename, not an alias.");
        RequireParent(files, parent, observed.ParentIdentity);
        return observed;
    }

    private static PackageStoreAdmissionException Unknown(string message)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message);
}
