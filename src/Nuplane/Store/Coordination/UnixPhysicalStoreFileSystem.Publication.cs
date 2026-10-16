using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Unix;

namespace Nuplane.Store.Coordination;

internal sealed partial class UnixPhysicalStoreFileSystem : IPhysicalStorePublicationFileSystem
{
    /// <inheritdoc />
    public PhysicalStoreEntryInfo PublishControlFileAt(
        PhysicalStoreDirectoryHandle parent,
        string stagedName,
        PhysicalFileIdentity expectedStagedIdentity,
        string destinationName,
        PhysicalFileIdentity? expectedDestinationIdentity)
    {
        var platform = RequireSupportedPlatform();
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(expectedStagedIdentity);
        ValidateName(stagedName);
        ValidateName(destinationName);

        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var parentFd = GetFileDescriptor(parentLease);
        var prepared = PhysicalStorePublicationChecks.PreparePublish(
            this,
            this,
            parent,
            stagedName,
            expectedStagedIdentity,
            destinationName,
            expectedDestinationIdentity);
        RequirePublicationNameProfile(platform, parentFd, prepared.Semantics);
        PhysicalStorePublicationChecks.RequireParent(this, parent, prepared.ParentIdentity);

        // Repeat identity/type/link checks immediately before the namespace transition. The
        // caller's root/member owners serialize cooperating writers; this is not a CAS against
        // arbitrary filesystem writers.
        PhysicalStorePublicationChecks.RequireExpectedEntry(
            InspectChildNoFollow(parent, stagedName),
            expectedStagedIdentity);
        PhysicalStorePublicationChecks.RequireExpectedEntry(
            InspectChildNoFollow(parent, destinationName),
            expectedDestinationIdentity);
        RequirePublicationNameProfile(platform, parentFd, prepared.Semantics);

        InvokeNative(
            expectedDestinationIdentity is null ? "atomically publish a new control file" : "atomically replace a control file",
            () => expectedDestinationIdentity is null
                ? UnixNative.PublishNoReplaceAt(platform, parentFd, stagedName, destinationName)
                : UnixNative.PublishReplaceAt(platform, parentFd, stagedName, destinationName));

        RequirePublicationNameProfile(platform, parentFd, prepared.Semantics);
        return PhysicalStorePublicationChecks.VerifyPublished(
            this,
            this,
            parent,
            stagedName,
            destinationName,
            prepared);
    }

    /// <inheritdoc />
    public void RemoveControlFileAt(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedIdentity)
        => RemoveExactRegularFileAt(
            parent,
            singleName,
            expectedIdentity,
            "transaction control file",
            requireSameVolume: false);

    private void RemoveExactRegularFileAt(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedIdentity,
        string objectDescription,
        bool requireSameVolume)
    {
        var platform = RequireSupportedPlatform();
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        ValidateName(singleName);

        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var parentFd = GetFileDescriptor(parentLease);
        var prepared = PhysicalStorePublicationChecks.PrepareRemoval(
            this,
            this,
            parent,
            singleName,
            expectedIdentity);
        if (requireSameVolume)
            PhysicalStoreDeletionChecks.RequireSameVolume(prepared.ParentIdentity, prepared.FileIdentity);

        var profileOperation = requireSameVolume ? "coordinated namespace mutation" : "publication";
        RequirePublicationNameProfile(platform, parentFd, prepared.Semantics, profileOperation);
        PhysicalStorePublicationChecks.RequireParent(this, parent, prepared.ParentIdentity);

        // Removal is limited to the exact regular single-link artifact observed above. The
        // caller holds the coordinating owner; no recursive or path-based cleanup is allowed.
        PhysicalStorePublicationChecks.RequireExpectedEntry(
            InspectChildNoFollow(parent, singleName),
            expectedIdentity);
        RequirePublicationNameProfile(platform, parentFd, prepared.Semantics, profileOperation);

        InvokeNative(
            $"remove an exactly identified {objectDescription}",
            () => UnixNative.UnlinkFileAt(platform, parentFd, singleName, $"a held-parent {objectDescription}"));

        RequirePublicationNameProfile(platform, parentFd, prepared.Semantics, profileOperation);
        if (requireSameVolume)
            PhysicalStoreDeletionChecks.VerifyRemoved(this, this, parent, singleName, prepared);
        else
            PhysicalStorePublicationChecks.VerifyRemoved(this, parent, singleName, prepared);
    }

    private static void RequirePublicationNameProfile(
        UnixPlatform platform,
        int parentFd,
        PhysicalStoreNameSemantics expected,
        string operationDescription = "publication")
    {
        var observed = InvokeNative(
            $"inspect {operationDescription} directory name semantics",
            () => UnixNative.GetNameProfile(platform, parentFd));
        if (!string.Equals(observed.ProfileId, expected.ProfileId, StringComparison.Ordinal) ||
            observed.CaseSensitive != expected.CaseSensitive ||
            observed.NormalizationInsensitive != expected.NormalizationInsensitive)
        {
            throw Unknown($"The held parent native name profile changed during {operationDescription}.");
        }
    }
}
