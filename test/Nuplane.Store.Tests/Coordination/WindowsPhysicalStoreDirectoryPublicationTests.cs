using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles.Windows;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Windows")]
public sealed class WindowsPhysicalStoreDirectoryPublicationTests
{
    private const string FinalName = ".nuplane-store";

    [SupportedWindowsFact]
    public void NativeNoReplace_RefusesOccupiedDirectoryWithoutManagedPreinspection()
    {
        using var context = WindowsPhysicalStorePublicationTests.CreateContext();
        var stage = PhysicalStoreDirectoryPublicationTests.PrepareTree(context, "Prepared", "staged-ledger"u8.ToArray());
        var destination = PhysicalStoreDirectoryPublicationTests.PrepareTree(context, FinalName, "existing-ledger"u8.ToArray());

        using (var parent = WindowsPhysicalStorePublicationTests.OpenRawDirectoryPath(context.ParentPath))
        using (var staged = WindowsNative.OpenRelative(
                   parent.DangerousGetHandle(),
                   "Prepared",
                   WindowsNative.DeleteAccess | WindowsNative.FileReadAttributes |
                   WindowsNative.FileListDirectory | WindowsNative.Synchronize,
                   WindowsNative.FileOpen,
                   WindowsNative.FileDirectoryFile,
                   shareAccess: WindowsNative.ShareRead | WindowsNative.ShareWrite | WindowsNative.ShareDelete))
        {
            var stagedBefore = WindowsNative.QueryEntry(staged.DangerousGetHandle()).Identity;
            var destinationBefore = OpenDirectoryIdentity(parent.DangerousGetHandle(), FinalName);

            var error = Assert.Throws<WindowsNativeCallException>(() => WindowsNative.RenameDirectoryNoReplace(
                staged.DangerousGetHandle(), parent.DangerousGetHandle(), FinalName));

            Assert.Equal(WindowsNative.StatusObjectNameCollision, error.NtStatus);
            Assert.Equal(stagedBefore, WindowsNative.QueryEntry(staged.DangerousGetHandle()).Identity);
            Assert.Equal(destinationBefore, OpenDirectoryIdentity(parent.DangerousGetHandle(), FinalName));
        }

        PhysicalStoreDirectoryPublicationTests.AssertTree(context, "Prepared", stage);
        PhysicalStoreDirectoryPublicationTests.AssertTree(context, FinalName, destination);
    }

    [SupportedWindowsFact]
    public void Publish_RefusesOpenStagedDescendantAndSucceedsAfterItCloses()
    {
        using var context = WindowsPhysicalStorePublicationTests.CreateContext();
        var staged = PhysicalStoreDirectoryPublicationTests.PrepareTree(
            context, "Prepared", "staged-ledger"u8.ToArray());
        var directories = PhysicalStoreDirectoryPublicationTests.Directories(context);

        using var stagedDirectory = context.FileSystem.OpenDirectoryChildNoFollow(context.Parent, "Prepared");
        using var nestedDirectory = context.FileSystem.OpenDirectoryChildNoFollow(stagedDirectory, "nested");
        using var descendant = context.FileSystem.OpenFileChildNoFollow(nestedDirectory, "payload.bin", FileAccess.Read);
        nestedDirectory.Dispose();
        stagedDirectory.Dispose();

        var refusal = Assert.Throws<PackageStoreAdmissionException>(() => directories.PublishDirectoryNoReplaceAt(
            context.Parent, "Prepared", staged.Directory, FinalName));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        PhysicalStoreDirectoryPublicationTests.AssertTree(context, "Prepared", staged);
        Assert.Null(context.FileSystem.InspectChildNoFollow(context.Parent, FinalName));

        descendant.Dispose();
        var published = directories.PublishDirectoryNoReplaceAt(context.Parent, "Prepared", staged.Directory, FinalName);

        Assert.Equal(staged.Directory, published.Identity);
        PhysicalStoreDirectoryPublicationTests.AssertTree(context, FinalName, staged);
        Assert.Null(context.FileSystem.InspectChildNoFollow(context.Parent, "Prepared"));
    }

    private static WindowsNative.FileIdInfo OpenDirectoryIdentity(IntPtr parent, string name)
    {
        using var directory = WindowsNative.OpenRelative(
            parent,
            name,
            WindowsNative.FileReadAttributes | WindowsNative.FileListDirectory | WindowsNative.Synchronize,
            WindowsNative.FileOpen,
            WindowsNative.FileDirectoryFile,
            shareAccess: WindowsNative.ShareRead | WindowsNative.ShareWrite | WindowsNative.ShareDelete);
        return WindowsNative.QueryEntry(directory.DangerousGetHandle()).Identity;
    }

}
