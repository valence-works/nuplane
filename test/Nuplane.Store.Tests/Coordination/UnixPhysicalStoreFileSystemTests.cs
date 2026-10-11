using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Unix;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Unix")]
public sealed class UnixPhysicalStoreFileSystemTests
{
    [Fact]
    public void OpenNamespaceRoot_NonNamespaceAnchor_ReturnsTypedRefusal()
    {
        var adapter = new UnixPhysicalStoreFileSystem();
        var anchor = UnixPhysicalStoreFileSystem.IsSupportedPlatform ? Path.GetTempPath() : "/";

        var exception = Assert.Throws<PackageStoreAdmissionException>(() => adapter.OpenNamespaceRoot(anchor));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedFilesystem, exception.Reason);
    }

    [SupportedUnixFact]
    public void NativeChildOpen_RefusesFinalLinksWithoutManagedPreInspection()
    {
        using var fixture = new PackageStoreFixture();
        fixture.CreateDirectory("target");
        File.WriteAllText(fixture.GetPath("ordinary-file"), "owned payload");
        Directory.CreateSymbolicLink(fixture.GetPath("directory-link"), "target");
        File.CreateSymbolicLink(fixture.GetPath("file-link"), "ordinary-file");
        var platform = UnixNative.GetPlatform()!.Value;
        var currentDirectory = platform == UnixPlatform.Darwin ? -2 : -100;
        using var parent = new SafeFileHandle(
            (IntPtr)UnixNative.OpenDirectoryAt(platform, currentDirectory, ResolveRealPath(fixture.RootPath)),
            ownsHandle: true);
        var parentFd = checked((int)parent.DangerousGetHandle());

        Assert.Throws<UnixNativeCallException>(() =>
        {
            using var unexpected = new SafeFileHandle(
                (IntPtr)UnixNative.OpenDirectoryAt(platform, parentFd, "directory-link"), ownsHandle: true);
        });
        Assert.Throws<UnixNativeCallException>(() =>
        {
            using var unexpected = new SafeFileHandle(
                (IntPtr)UnixNative.OpenFileAt(platform, parentFd, "file-link", FileAccess.Read), ownsHandle: true);
        });
    }

    [SupportedUnixFact]
    public void ChildOperations_RejectTraversalAndLinkEntries()
    {
        using var fixture = new PackageStoreFixture();
        var adapter = new UnixPhysicalStoreFileSystem();
        using var root = OpenFixtureRoot(adapter, fixture);
        using var target = adapter.CreateDirectoryExclusiveAt(root, "target");
        File.WriteAllText(fixture.GetPath("ordinary-file"), "payload");
        Directory.CreateSymbolicLink(fixture.GetPath("directory-link"), "target");
        File.CreateSymbolicLink(fixture.GetPath("file-link"), "ordinary-file");
        CreateFifo(fixture.GetPath("named-pipe"));

        Assert.Throws<ArgumentException>(() => adapter.InspectChildNoFollow(root, "../outside"));

        var linkInfo = adapter.InspectChildNoFollow(root, "directory-link");
        Assert.NotNull(linkInfo);
        Assert.Equal(PhysicalStoreEntryKind.SymbolicLink, linkInfo.Kind);
        Assert.Equal("target", adapter.ReadLinkTargetNoFollow(root, "directory-link", linkInfo.Identity));

        var wrongIdentity = new PhysicalFileIdentity(
            linkInfo.Identity.Provider,
            linkInfo.Identity.VolumeOrDeviceId,
            linkInfo.Identity.FileId + "-wrong");
        var identityRefusal = Assert.Throws<PackageStoreAdmissionException>(
            () => adapter.ReadLinkTargetNoFollow(root, "directory-link", wrongIdentity));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, identityRefusal.Reason);

        var directoryRefusal = Assert.Throws<PackageStoreAdmissionException>(
            () => adapter.OpenDirectoryChildNoFollow(root, "directory-link"));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, directoryRefusal.Reason);

        var fileRefusal = Assert.Throws<PackageStoreAdmissionException>(
            () => adapter.OpenFileChildNoFollow(root, "file-link", FileAccess.Read));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, fileRefusal.Reason);

        var pipeInfo = adapter.InspectChildNoFollow(root, "named-pipe");
        Assert.NotNull(pipeInfo);
        Assert.Equal(PhysicalStoreEntryKind.Other, pipeInfo.Kind);
        var pipeRefusal = Assert.Throws<PackageStoreAdmissionException>(
            () => adapter.OpenFileChildNoFollow(root, "named-pipe", FileAccess.Read));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, pipeRefusal.Reason);

        using var parent = adapter.OpenParentDirectory(target);
        Assert.Equal(adapter.InspectHandle(root).Identity, adapter.InspectHandle(parent).Identity);
    }

    [SupportedUnixFact]
    public void ControlFiles_AreExclusiveBoundedAndSingleLink()
    {
        using var fixture = new PackageStoreFixture();
        var adapter = new UnixPhysicalStoreFileSystem();
        using var root = OpenFixtureRoot(adapter, fixture);
        var contents = "owned-control-data"u8.ToArray();
        using var created = adapter.CreateFileExclusiveAt(root, "control");

        adapter.WriteNewControlFile(created, contents);

        Assert.Equal(contents, adapter.ReadControlFile(created, contents.Length));
        Assert.Throws<InvalidOperationException>(() => adapter.WriteNewControlFile(created, "second write"u8.ToArray()));
        var replacementRefusal = Assert.Throws<PackageStoreAdmissionException>(() => adapter.CreateFileExclusiveAt(root, "control"));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, replacementRefusal.Reason);

        var boundRefusal = Assert.Throws<PackageStoreAdmissionException>(() => adapter.ReadControlFile(created, contents.Length - 1));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, boundRefusal.Reason);

        using var reopened = adapter.OpenFileChildNoFollow(root, "control", FileAccess.Read);
        Assert.Equal(contents, adapter.ReadControlFile(reopened, contents.Length));

        var hardLinkPath = fixture.GetPath("control-hardlink");
        CreateHardLink(fixture.GetPath("control"), hardLinkPath);
        var hardLinkInfo = adapter.InspectChildNoFollow(root, "control-hardlink");
        Assert.NotNull(hardLinkInfo);
        Assert.Equal(2UL, hardLinkInfo.LinkCount);

        var hardLinkRefusal = Assert.Throws<PackageStoreAdmissionException>(
            () => adapter.OpenFileChildNoFollow(root, "control-hardlink", FileAccess.Read));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, hardLinkRefusal.Reason);
    }

    [SupportedUnixFact]
    [UnsupportedOSPlatform("windows")]
    public void ExclusiveCreation_UsesPrivateDirectoryAndFileModes()
    {
        using var fixture = new PackageStoreFixture();
        var adapter = new UnixPhysicalStoreFileSystem();
        using var root = OpenFixtureRoot(adapter, fixture);
        using var directory = adapter.CreateDirectoryExclusiveAt(root, "private-directory");
        using var file = adapter.CreateFileExclusiveAt(root, "private-control");
        const UnixFileMode PermissionMask =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(fixture.GetPath("private-directory")) & PermissionMask);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(fixture.GetPath("private-control")) & PermissionMask);
    }

    [SupportedUnixFact]
    public void HeldDirectory_KeepsPhysicalIdentityAcrossTextualReplacement()
    {
        using var fixture = new PackageStoreFixture();
        var adapter = new UnixPhysicalStoreFileSystem();
        using var root = OpenFixtureRoot(adapter, fixture);
        using var held = adapter.CreateDirectoryExclusiveAt(root, "held");
        var originalIdentity = adapter.InspectHandle(held).Identity;

        Directory.Move(fixture.GetPath("held"), fixture.GetPath("moved"));
        Directory.CreateDirectory(fixture.GetPath("held"));
        using var replacement = adapter.OpenDirectoryChildNoFollow(root, "held");

        Assert.Equal(originalIdentity, adapter.InspectHandle(held).Identity);
        Assert.NotEqual(originalIdentity, adapter.InspectHandle(replacement).Identity);
        using var moved = adapter.OpenDirectoryChildNoFollow(root, "moved");
        Assert.Equal(originalIdentity, adapter.InspectHandle(moved).Identity);
    }

    [SupportedUnixFact]
    public async Task ExclusiveLock_IsNonblockingAndRemainsHeldAfterSourceWrapperDisposal()
    {
        using var fixture = new PackageStoreFixture();
        var adapter = new UnixPhysicalStoreFileSystem();
        using var root = OpenFixtureRoot(adapter, fixture);
        using var original = adapter.CreateFileExclusiveAt(root, "lock");
        using var contender = adapter.OpenFileChildNoFollow(root, "lock", FileAccess.ReadWrite);
        using var postReleaseProbe = adapter.OpenFileChildNoFollow(root, "lock", FileAccess.ReadWrite);

        await using var firstOwner = Assert.IsAssignableFrom<IAsyncDisposable>(await adapter.TryAcquireExclusiveLock(original));
        Assert.Null(await adapter.TryAcquireExclusiveLock(original));
        Assert.Null(await adapter.TryAcquireExclusiveLock(contender));

        original.Dispose();
        Assert.Null(await adapter.TryAcquireExclusiveLock(postReleaseProbe));

        await firstOwner.DisposeAsync();
        await using var afterRelease = Assert.IsAssignableFrom<IAsyncDisposable>(await adapter.TryAcquireExclusiveLock(postReleaseProbe));
        await afterRelease.DisposeAsync();
    }

    [SupportedUnixFact]
    public void Handles_RefuseForeignProviderAndExpiredScope()
    {
        using var fixture = new PackageStoreFixture();
        var adapter = new UnixPhysicalStoreFileSystem();
        using var root = OpenFixtureRoot(adapter, fixture);
        var file = adapter.CreateFileExclusiveAt(root, "owned");
        var foreignAdapter = new UnixPhysicalStoreFileSystem();

        var foreign = Assert.Throws<PackageStoreAdmissionException>(() => foreignAdapter.InspectHandle(file));
        Assert.Equal(PackageStoreAdmissionReason.RootMismatch, foreign.Reason);

        file.Dispose();
        var expired = Assert.Throws<PackageStoreAdmissionException>(() => adapter.InspectHandle(file));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, expired.Reason);
    }

    private static PhysicalStoreDirectoryHandle OpenFixtureRoot(UnixPhysicalStoreFileSystem adapter, PackageStoreFixture fixture)
    {
        var canonicalPath = ResolveRealPath(fixture.RootPath);
        var current = adapter.OpenNamespaceRoot("/");
        try
        {
            foreach (var segment in canonicalPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = adapter.OpenDirectoryChildNoFollow(current, segment);
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

    private static string ResolveRealPath(string path)
    {
        var resolved = OperatingSystem.IsMacOS()
            ? UnixPath.DarwinRealPath(path, IntPtr.Zero)
            : UnixPath.LinuxRealPath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
            throw new IOException($"The owned test fixture could not be physically resolved (native error {Marshal.GetLastPInvokeError()}).");

        try
        {
            return Marshal.PtrToStringUTF8(resolved) ?? throw new IOException("The native path resolver returned no path.");
        }
        finally
        {
            UnixPath.Free(resolved);
        }
    }

    private static void CreateHardLink(string existingPath, string newPath)
    {
        var result = OperatingSystem.IsMacOS()
            ? UnixPath.DarwinLink(existingPath, newPath)
            : UnixPath.LinuxLink(existingPath, newPath);
        if (result != 0)
            throw new IOException($"The owned hard-link fixture could not be created (native error {Marshal.GetLastPInvokeError()}).");
    }

    private static void CreateFifo(string path)
    {
        var result = OperatingSystem.IsMacOS()
            ? UnixPath.DarwinMakeFifo(path, 0x180)
            : UnixPath.LinuxMakeFifo(path, 0x180);
        if (result != 0)
            throw new IOException($"The owned FIFO fixture could not be created (native error {Marshal.GetLastPInvokeError()}).");
    }

    private static class UnixPath
    {
        [DllImport("libSystem.B.dylib", EntryPoint = "realpath", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern IntPtr DarwinRealPath(string path, IntPtr resolvedPath);

        [DllImport("libc", EntryPoint = "realpath", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern IntPtr LinuxRealPath(string path, IntPtr resolvedPath);

        [DllImport("libSystem.B.dylib", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int DarwinLink(string existingPath, string newPath);

        [DllImport("libc", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int LinuxLink(string existingPath, string newPath);

        [DllImport("libSystem.B.dylib", EntryPoint = "mkfifo", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int DarwinMakeFifo(string path, uint mode);

        [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int LinuxMakeFifo(string path, uint mode);

        [DllImport("libSystem.B.dylib", EntryPoint = "free")]
        private static extern void DarwinFree(IntPtr value);

        [DllImport("libc", EntryPoint = "free")]
        private static extern void LinuxFree(IntPtr value);

        internal static void Free(IntPtr value)
        {
            if (OperatingSystem.IsMacOS())
                DarwinFree(value);
            else
                LinuxFree(value);
        }
    }
}

public sealed class SupportedUnixFactAttribute : FactAttribute
{
    public SupportedUnixFactAttribute()
    {
        if (!UnixPhysicalStoreFileSystem.IsSupportedPlatform)
            Skip = "Native Unix operations are qualified only on Darwin arm64 and Linux x64/arm64; this skip is not runtime acceptance.";
    }
}
