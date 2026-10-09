using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Unix;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Unix")]
public sealed class UnixPhysicalStoreDirectoryPublicationTests
{
    [SupportedUnixFact]
    public void NativeNoReplace_RefusesOccupiedDirectoryWithoutManagedPreinspection()
    {
        using var context = UnixPhysicalStorePublicationTests.CreateContext();
        using (context.FileSystem.CreateDirectoryExclusiveAt(context.Parent, "staged")) { }
        using (context.FileSystem.CreateDirectoryExclusiveAt(context.Parent, "destination")) { }

        var platform = UnixNative.GetPlatform()!.Value;
        using var parent = UnixPhysicalStorePublicationTests.OpenDirectoryPath(platform, context.ParentPath);
        var parentFd = checked((int)parent.DangerousGetHandle());
        var stagedBefore = RequireDirectory(platform, parentFd, "staged");
        var destinationBefore = RequireDirectory(platform, parentFd, "destination");

        var error = Assert.Throws<UnixNativeCallException>(
            () => UnixNative.PublishDirectoryNoReplaceAt(platform, parentFd, "staged", "destination"));

        Assert.Equal(17, error.Error); // EEXIST on the qualified Darwin and Linux ABIs.
        Assert.Equal(stagedBefore, RequireDirectory(platform, parentFd, "staged"));
        Assert.Equal(destinationBefore, RequireDirectory(platform, parentFd, "destination"));
    }

    [SupportedUnixFact]
    public void Publish_UsesHeldParentAfterItsTextualNameIsReplaced()
    {
        using var context = UnixPhysicalStorePublicationTests.CreateContext();
        var movedParentPath = context.ParentPath + "-moved";
        Directory.Move(context.ParentPath, movedParentPath);
        Directory.CreateDirectory(context.ParentPath);

        PhysicalFileIdentity stagedIdentity;
        using (var staged = context.FileSystem.CreateDirectoryExclusiveAt(context.Parent, "staged"))
            stagedIdentity = context.FileSystem.InspectHandle(staged).Identity;

        var publication = (IPhysicalStoreDirectoryPublicationFileSystem)context.FileSystem;
        var result = publication.PublishDirectoryNoReplaceAt(context.Parent, "staged", stagedIdentity, "published");

        Assert.Equal(PhysicalStoreEntryKind.Directory, result.Kind);
        Assert.Equal(stagedIdentity, result.Identity);
        Assert.Null(context.FileSystem.InspectChildNoFollow(context.Parent, "staged"));
        Assert.Equal(stagedIdentity, context.FileSystem.InspectChildNoFollow(context.Parent, "published")!.Identity);
        Assert.True(Directory.Exists(Path.Combine(movedParentPath, "published")));
        Assert.False(Directory.Exists(Path.Combine(context.ParentPath, "published")));
    }

    [LinuxExt4CasefoldFact]
    public void Publish_Ext4CasefoldAliasIsObservedButOnlyCanonicalNameCanMove()
    {
        using var context = UnixPhysicalStorePublicationTests.CreateContext(enableCasefold: true);
        PhysicalFileIdentity stagedIdentity;
        using (var staged = context.FileSystem.CreateDirectoryExclusiveAt(context.Parent, "Stage"))
            stagedIdentity = context.FileSystem.InspectHandle(staged).Identity;

        var directoryPublication = (IPhysicalStoreDirectoryPublicationFileSystem)context.FileSystem;
        var observed = directoryPublication.ObserveCanonicalDirectoryNameNoFollow(
            context.Parent,
            "stage",
            stagedIdentity);
        Assert.Equal("Stage", observed.Basename);
        Assert.Equal("linux-ext4-casefold-v1", observed.Semantics.ProfileId);
        Assert.False(observed.Semantics.CaseSensitive);
        Assert.True(observed.Semantics.NormalizationInsensitive);

        Assert.Throws<PackageStoreAdmissionException>(
            () => directoryPublication.PublishDirectoryNoReplaceAt(context.Parent, "stage", stagedIdentity, "Published"));
        Assert.Equal(stagedIdentity, context.FileSystem.InspectChildNoFollow(context.Parent, "Stage")!.Identity);
        Assert.Null(context.FileSystem.InspectChildNoFollow(context.Parent, "Published"));

        var result = directoryPublication.PublishDirectoryNoReplaceAt(context.Parent, "Stage", stagedIdentity, "Published");
        Assert.Equal(stagedIdentity, result.Identity);
        Assert.Null(context.FileSystem.InspectChildNoFollow(context.Parent, "Stage"));
        Assert.Equal(stagedIdentity, context.FileSystem.InspectChildNoFollow(context.Parent, "Published")!.Identity);
    }

    private static UnixMetadata RequireDirectory(UnixPlatform platform, int parentFd, string name)
    {
        var result = UnixNative.StatAt(platform, parentFd, name);
        Assert.Equal(UnixStatResultStatus.Success, result.Status);
        Assert.Equal(UnixNative.FileTypeDirectory, result.Metadata.Mode & UnixNative.FileTypeMask);
        return result.Metadata;
    }

}
