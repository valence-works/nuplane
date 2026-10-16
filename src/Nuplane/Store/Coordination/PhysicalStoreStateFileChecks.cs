using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Checks state-only entry observations without weakening shared control/package validation.</summary>
internal static class PhysicalStoreStateFileChecks
{
    internal static void RequireParent(PhysicalStoreEntryInfo actual, StateSlotIdentity slot)
    {
        if (actual.Kind != PhysicalStoreEntryKind.Directory || actual.Identity != slot.ParentIdentity)
            throw Unknown("The held state parent differs from the retained slot parent.");
    }

    internal static void RequireRegular(PhysicalStoreEntryInfo entry)
    {
        if (entry.Kind != PhysicalStoreEntryKind.RegularFile || entry.LinkCount == 0)
            throw Unknown("State payload reads require a regular file with a live directory entry.");
    }

    internal static void RequireEntry(PhysicalStoreEntryInfo? actual, PhysicalStoreEntryInfo? expected)
    {
        if (actual != expected)
            throw Unknown("The selected state entry changed identity, kind, length, link count or reparse tag.");
        if (expected is not null &&
            (expected.LinkCount == 0 || expected.Kind is not (PhysicalStoreEntryKind.RegularFile or PhysicalStoreEntryKind.SymbolicLink)))
        {
            throw Unknown("The selected state destination is not an ordinary file or symbolic link.");
        }
    }

    internal static PhysicalStoreCanonicalName PreparePublication(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        PhysicalStoreDirectoryHandle parent,
        string stagedName,
        PhysicalFileIdentity stagedIdentity,
        StateSlotIdentity destination,
        PhysicalStoreEntryInfo? prior)
    {
        PhysicalStoreNames.ValidateSingleComponent(stagedName);
        if (string.Equals(stagedName, destination.CanonicalBasename, StringComparison.Ordinal) ||
            stagedIdentity == prior?.Identity)
            throw Unknown("State publication requires distinct staged and destination entries.");
        PhysicalStorePublicationChecks.RequireExpectedEntry(files.InspectChildNoFollow(parent, stagedName), stagedIdentity);
        var staged = names.ObserveCanonicalFileNameNoFollow(parent, stagedName, stagedIdentity);
        if (!string.Equals(staged.Basename, stagedName, StringComparison.Ordinal) ||
            staged.ParentIdentity != destination.ParentIdentity || staged.Semantics != destination.NameSemantics)
            throw Unknown("The staged state file is not one exact canonical file beneath the retained parent/profile.");
        RequireParent(files.InspectHandle(parent), destination);
        RequireEntry(files.InspectChildNoFollow(parent, destination.CanonicalBasename), prior);
        return staged;
    }

    internal static PhysicalStoreEntryInfo VerifyPublication(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        PhysicalStoreDirectoryHandle parent,
        string stagedName,
        StateSlotIdentity destination,
        PhysicalStoreCanonicalName staged)
    {
        RequireParent(files.InspectHandle(parent), destination);
        if (names.ObserveDirectoryNameSemantics(parent) != destination.NameSemantics)
            throw Unknown("The state publication parent lookup profile changed.");
        var published = PhysicalStorePublicationChecks.RequireExpectedEntry(
            files.InspectChildNoFollow(parent, destination.CanonicalBasename), staged.FileIdentity)!;
        if (files.InspectChildNoFollow(parent, stagedName) is not null)
            throw Unknown("The staged state name remains present after publication.");
        RequireParent(files.InspectHandle(parent), destination);
        return published;
    }

    private static PackageStoreAdmissionException Unknown(string message)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message);
}
