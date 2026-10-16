using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Unix;

namespace Nuplane.Store.Coordination;

internal sealed partial class UnixPhysicalStoreFileSystem : IPhysicalStoreDeletionFileSystem
{
    /// <inheritdoc />
    public void RemoveFileAt(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedIdentity)
        => RemoveExactRegularFileAt(
            parent,
            singleName,
            expectedIdentity,
            "package file",
            requireSameVolume: true);

    /// <inheritdoc />
    public void RemoveEmptyDirectoryAt(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedIdentity)
    {
        var platform = RequireSupportedPlatform();
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        ValidateName(singleName);

        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var parentFd = GetFileDescriptor(parentLease);
        var prepared = PhysicalStoreDeletionChecks.PrepareDirectoryRemoval(
            this,
            this,
            parent,
            singleName,
            expectedIdentity);

        PhysicalStoreDeletionChecks.RevalidateDirectoryRemoval(this, this, parent, singleName, prepared);
        RequirePublicationNameProfile(platform, parentFd, prepared.Semantics, "coordinated namespace mutation");
        PhysicalStorePublicationChecks.RequireParent(this, parent, prepared.ParentIdentity);

        InvokeNative(
            "remove an exactly identified empty directory",
            () => UnixNative.UnlinkEmptyDirectoryAt(platform, parentFd, singleName));

        RequirePublicationNameProfile(platform, parentFd, prepared.Semantics, "coordinated namespace mutation");
        PhysicalStoreDeletionChecks.VerifyRemoved(this, this, parent, singleName, prepared);
    }
}
