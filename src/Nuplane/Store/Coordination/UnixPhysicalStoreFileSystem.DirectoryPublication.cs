using System.Text;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Unix;
using static Nuplane.Store.Coordination.PhysicalStoreDirectoryPublicationChecks;

namespace Nuplane.Store.Coordination;

internal sealed partial class UnixPhysicalStoreFileSystem
{
    /// <inheritdoc />
    public PhysicalStoreCanonicalName ObserveCanonicalDirectoryNameNoFollow(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedDirectoryIdentity)
    {
        var platform = RequireSupportedPlatform();
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(expectedDirectoryIdentity);
        ValidateName(singleName);

        var parentBefore = InspectHandle(parent);
        RequireDirectory(parentBefore, "Canonical-name observation requires a held directory parent.");
        RequireExpectedDirectory(InspectChildNoFollow(parent, singleName), expectedDirectoryIdentity);

        using var original = OpenDirectoryChildNoFollow(parent, singleName);
        var directoryBefore = InspectHandle(original);
        RequireExpectedDirectory(directoryBefore, expectedDirectoryIdentity);

        UnixNameProfile profileBefore;
        byte[] nameBytes;
        using (var parentScope = parent.AcquireScopedSafeHandle(_providerToken))
        using (var directoryScope = original.AcquireScopedSafeHandle(_providerToken))
        {
            var parentFd = GetFileDescriptor(parentScope);
            var directoryFd = GetFileDescriptor(directoryScope);
            profileBefore = InvokeNative(
                "inspect directory name semantics",
                () => UnixNative.GetNameProfile(platform, parentFd));
            nameBytes = InvokeNative(
                "observe canonical directory-entry name",
                () => UnixNative.FindDirectoryEntryName(platform, parentFd, directoryFd, MaximumCanonicalNameBytes));
        }

        string canonicalBasename;
        try
        {
            canonicalBasename = StrictUtf8.GetString(nameBytes);
            PhysicalStoreNames.ValidateSingleComponent(canonicalBasename);
        }
        catch (Exception exception) when (exception is DecoderFallbackException or ArgumentException)
        {
            throw Unknown("The native directory entry did not provide one valid UTF-8 basename.", exception);
        }

        RequireExpectedDirectory(InspectChildNoFollow(parent, canonicalBasename), expectedDirectoryIdentity);
        using var canonical = OpenDirectoryChildNoFollow(parent, canonicalBasename);
        var canonicalInfo = InspectHandle(canonical);
        var directoryAfter = InspectHandle(original);
        var parentAfter = InspectHandle(parent);
        var suppliedAfter = InspectChildNoFollow(parent, singleName);
        var profileAfter = InvokeNative(
            "recheck directory name semantics",
            () =>
            {
                using var parentScope = parent.AcquireScopedSafeHandle(_providerToken);
                return UnixNative.GetNameProfile(platform, GetFileDescriptor(parentScope));
            });

        RequireExpectedDirectory(canonicalInfo, expectedDirectoryIdentity);
        RequireExpectedDirectory(directoryAfter, expectedDirectoryIdentity);
        RequireExpectedDirectory(suppliedAfter, expectedDirectoryIdentity);
        RequireDirectory(parentAfter, "The held canonical-name parent changed kind during observation.");
        if (parentAfter.Identity != parentBefore.Identity || profileAfter != profileBefore)
            throw Unknown("The parent or native name profile changed during directory-name observation.");

        return new PhysicalStoreCanonicalName(
            parentBefore.Identity,
            expectedDirectoryIdentity,
            canonicalBasename,
            new PhysicalStoreNameSemantics(
                profileBefore.ProfileId,
                PhysicalStoreNameEncoding.Utf8,
                profileBefore.CaseSensitive,
                profileBefore.NormalizationInsensitive));
    }

    /// <inheritdoc />
    public PhysicalStoreEntryInfo PublishDirectoryNoReplaceAt(
        PhysicalStoreDirectoryHandle parent,
        string stagedName,
        PhysicalFileIdentity expectedStagedIdentity,
        string destinationName)
    {
        var platform = RequireSupportedPlatform();
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(expectedStagedIdentity);
        ValidateName(stagedName);
        ValidateName(destinationName);

        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var parentFd = GetFileDescriptor(parentLease);
        var prepared = PhysicalStoreDirectoryPublicationChecks.PreparePublish(
            this,
            this,
            parent,
            stagedName,
            expectedStagedIdentity,
            destinationName);
        RequirePublicationNameProfile(platform, parentFd, prepared.Semantics);
        PhysicalStorePublicationChecks.RequireParent(this, parent, prepared.ParentIdentity);

        // Repeat exact kind/identity and destination-absence checks immediately before the
        // native transition. Coordinating owners serialize admitted writers; arbitrary external
        // namespace mutation is not a compare-and-swap guarantee.
        var sourceBeforeMove = ObserveCanonicalDirectoryNameNoFollow(parent, stagedName, expectedStagedIdentity);
        if (!string.Equals(sourceBeforeMove.Basename, stagedName, StringComparison.Ordinal) ||
            sourceBeforeMove.ParentIdentity != prepared.ParentIdentity ||
            sourceBeforeMove.FileIdentity != expectedStagedIdentity ||
            sourceBeforeMove.Semantics != prepared.Semantics)
        {
            throw Unknown("The exact canonical staged directory name changed before publication.");
        }
        RequireExpectedDirectory(InspectChildNoFollow(parent, stagedName), expectedStagedIdentity);
        PhysicalStorePublicationChecks.RequireExpectedEntry(InspectChildNoFollow(parent, destinationName), null);
        PhysicalStorePublicationChecks.RequireParent(this, parent, prepared.ParentIdentity);
        RequirePublicationNameProfile(platform, parentFd, prepared.Semantics);

        InvokeNative(
            "atomically publish a prepared directory without replacement",
            () => UnixNative.PublishDirectoryNoReplaceAt(platform, parentFd, stagedName, destinationName));

        RequirePublicationNameProfile(platform, parentFd, prepared.Semantics);
        return PhysicalStoreDirectoryPublicationChecks.VerifyPublished(
            this,
            this,
            parent,
            stagedName,
            destinationName,
            prepared);
    }

    private static void RequireDirectory(PhysicalStoreEntryInfo entry, string message)
    {
        if (entry.Kind != PhysicalStoreEntryKind.Directory)
            throw Unknown(message);
    }
}
