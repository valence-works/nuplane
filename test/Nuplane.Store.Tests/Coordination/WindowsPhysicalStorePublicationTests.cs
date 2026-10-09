using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Windows;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Windows")]
public sealed class WindowsPhysicalStorePublicationTests
{
    [SupportedWindowsFact]
    public void Publish_ReplacesExistingSlotAndPreservesItsIdentity()
    {
        using var context = CreateContext();
        PhysicalStorePublicationTestCases.ReplacesExistingSlotAndPreservesItsIdentity(context);
    }

    [SupportedWindowsFact]
    public void Publish_AbsentSlotAndRefusesOccupiedTarget()
    {
        using (var context = CreateContext())
            PhysicalStorePublicationTestCases.PublishesToAbsentSlotWithoutReplacingAnything(context);
        using (var context = CreateContext())
            PhysicalStorePublicationTestCases.AbsentPublicationRefusesOccupiedTargetAndPreservesBothFiles(context);
    }

    [SupportedWindowsFact]
    public void Publish_RefusesStaleSourceAndDestinationIdentities()
    {
        using var context = CreateContext();
        PhysicalStorePublicationTestCases.StaleSourceAndDestinationIdentitiesRefuseWithoutMutation(context);
    }

    [SupportedWindowsFact]
    public void PublishAndRemove_RefuseSymbolicAndHardLinks()
    {
        using (var context = CreateContext())
            PhysicalStorePublicationTestCases.LinkEntriesAreRefusedForPublishAndRemove(context);
        using (var context = CreateContext())
            PhysicalStorePublicationTestCases.HardLinkedEntriesAreRefusedForPublishAndRemove(context, CreateHardLink);
    }

    [SupportedWindowsFact]
    public void Remove_RequiresExactRegularSingleLinkIdentity()
    {
        using var context = CreateContext();
        PhysicalStorePublicationTestCases.RemovalRequiresExactRegularSingleLinkIdentity(context);
    }

    [SupportedWindowsFact]
    public void Publish_RefusesAliasSpellingForeignAndClosedParents()
    {
        using var context = CreateContext();
        PhysicalStorePublicationTestCases.RefusesAliasSpellingAndForeignOrClosedParent(context);
    }

    [SupportedWindowsFact]
    public void Publish_HeldParentPreventsTextualRename()
    {
        using var context = CreateContext();
        PhysicalStorePublicationTestCases.WindowsHeldParentPreventsTextualRename(context);
    }

    [SupportedWindowsFact]
    public void NativeNoReplace_RefusesOccupiedTargetWithoutManagedPreInspection()
    {
        using var fixture = new PackageStoreFixture();
        var parentPath = fixture.CreateDirectory("parent");
        var prior = "prior-state"u8.ToArray();
        var staged = "next-state"u8.ToArray();
        File.WriteAllBytes(Path.Combine(parentPath, "state.json"), prior);
        File.WriteAllBytes(Path.Combine(parentPath, "state.next"), staged);

        using var parent = OpenRawDirectoryPath(parentPath);
        WindowsNative.FileIdInfo priorBefore;
        using (var priorFile = WindowsNative.OpenRelative(
                   parent.DangerousGetHandle(), "state.json",
                   WindowsNative.FileReadAttributes | WindowsNative.Synchronize,
                   WindowsNative.FileOpen, WindowsNative.FileNonDirectoryFile))
            priorBefore = WindowsNative.QueryEntry(priorFile.DangerousGetHandle()).Identity;

        WindowsNative.FileIdInfo stagedBefore;
        using (var stagedFile = WindowsNative.OpenRelative(
                   parent.DangerousGetHandle(),
                   "state.next",
                   WindowsNative.DeleteAccess | WindowsNative.FileReadAttributes | WindowsNative.Synchronize,
                   WindowsNative.FileOpen,
                   WindowsNative.FileNonDirectoryFile,
                   shareAccess: WindowsNative.ShareRead | WindowsNative.ShareWrite | WindowsNative.ShareDelete))
        {
            stagedBefore = WindowsNative.QueryEntry(stagedFile.DangerousGetHandle()).Identity;
            var error = Assert.Throws<WindowsNativeCallException>(() => WindowsNative.RenameControlFile(
                stagedFile.DangerousGetHandle(), parent.DangerousGetHandle(), "state.json", replace: false));
            Assert.Equal(WindowsNative.StatusObjectNameCollision, error.NtStatus);
        }

        using var stagedAfter = WindowsNative.OpenRelative(
            parent.DangerousGetHandle(), "state.next",
            WindowsNative.FileReadAttributes | WindowsNative.Synchronize,
            WindowsNative.FileOpen, WindowsNative.FileNonDirectoryFile);
        using var priorAfter = WindowsNative.OpenRelative(
            parent.DangerousGetHandle(), "state.json",
            WindowsNative.FileReadAttributes | WindowsNative.Synchronize,
            WindowsNative.FileOpen, WindowsNative.FileNonDirectoryFile);
        Assert.Equal(stagedBefore, WindowsNative.QueryEntry(stagedAfter.DangerousGetHandle()).Identity);
        Assert.Equal(priorBefore, WindowsNative.QueryEntry(priorAfter.DangerousGetHandle()).Identity);
        Assert.Equal(prior, File.ReadAllBytes(Path.Combine(parentPath, "state.json")));
        Assert.Equal(staged, File.ReadAllBytes(Path.Combine(parentPath, "state.next")));
    }

    internal static PhysicalStorePublicationTestContext CreateContext()
    {
        var fixture = new PackageStoreFixture();
        PhysicalStoreDirectoryHandle? parent = null;
        try
        {
            var adapter = new WindowsPhysicalStoreFileSystem();
            using (var root = OpenFixtureRoot(adapter, fixture))
                parent = adapter.CreateDirectoryExclusiveAt(root, "parent");

            return new PhysicalStorePublicationTestContext(
                fixture,
                adapter,
                (IPhysicalStorePublicationFileSystem)adapter,
                parent ?? throw new InvalidOperationException("The fixture parent was not created."));
        }
        catch
        {
            parent?.Dispose();
            fixture.Dispose();
            throw;
        }
    }

    private static PhysicalStoreDirectoryHandle OpenFixtureRoot(WindowsPhysicalStoreFileSystem adapter, PackageStoreFixture fixture)
    {
        var fullPath = Path.GetFullPath(fixture.RootPath);
        var anchor = Path.GetPathRoot(fullPath) ?? throw new IOException("The test fixture has no Windows volume root.");
        var current = adapter.OpenNamespaceRoot(anchor);
        try
        {
            foreach (var component in fullPath[anchor.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = adapter.OpenDirectoryChildNoFollow(current, component);
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    internal static SafeFileHandle OpenRawDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var anchor = Path.GetPathRoot(fullPath) ?? throw new IOException("The test fixture has no Windows volume root.");
        var current = WindowsNative.OpenNamespaceRoot(anchor);
        try
        {
            foreach (var component in fullPath[anchor.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                var next = WindowsNative.OpenRelative(
                    current.DangerousGetHandle(),
                    component,
                    WindowsNative.FileReadAttributes | WindowsNative.FileListDirectory | WindowsNative.Synchronize,
                    WindowsNative.FileOpen,
                    WindowsNative.FileDirectoryFile);
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static void CreateHardLink(string existingPath, string newPath)
    {
        if (!CreateHardLinkW(newPath, existingPath, IntPtr.Zero))
            throw new IOException($"The owned hard-link fixture could not be created (native error {Marshal.GetLastPInvokeError()}).");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);
}
