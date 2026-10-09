using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Windows;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Windows")]
public sealed class WindowsPhysicalStoreFileSystemTests
{
    [Fact]
    public void UnsupportedRuntime_RefusesWithTypedCapabilityResult()
    {
        if (WindowsPhysicalStoreFileSystem.IsSupportedPlatform)
            return;

        var adapter = new WindowsPhysicalStoreFileSystem();
        var error = Assert.Throws<PackageStoreAdmissionException>(() => adapter.OpenNamespaceRoot("C:\\"));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedFilesystem, error.Reason);
    }

    [SupportedWindowsFact]
    public void NamespaceAnchors_RejectNonRootsAndDriveRootMatchesVolumeGuidIdentity()
    {
        var adapter = new WindowsPhysicalStoreFileSystem();
        foreach (var invalid in new[] { "C:", "C:\\folder", "\\\\server\\share\\", "relative", "\\\\?\\Volume{not-a-guid}\\" })
        {
            var error = Assert.Throws<PackageStoreAdmissionException>(() => adapter.OpenNamespaceRoot(invalid));
            Assert.Equal(PackageStoreAdmissionReason.UnsupportedFilesystem, error.Reason);
        }

        using var driveRoot = adapter.OpenNamespaceRoot("C:\\");
        var volumeRootPath = WindowsNative.GetVolumeGuidRoot("C:\\");
        using var volumeRoot = adapter.OpenNamespaceRoot(volumeRootPath);

        Assert.Equal(adapter.InspectHandle(driveRoot).Identity, adapter.InspectHandle(volumeRoot).Identity);
    }

    [SupportedWindowsFact]
    public void ParentWalk_UsesRetainedExactEdgesAndSurvivesCallerWrapperDisposal()
    {
        using var fixture = new PackageStoreFixture();
        var adapter = new WindowsPhysicalStoreFileSystem();
        using var root = OpenFixtureRoot(adapter, fixture);
        var rootIdentity = adapter.InspectHandle(root).Identity;
        using var child = adapter.CreateDirectoryExclusiveAt(root, "owned-child");
        var childIdentity = adapter.InspectHandle(child).Identity;
        using var grandchild = adapter.CreateDirectoryExclusiveAt(child, "owned-grandchild");
        using var resolvedChild = adapter.OpenParentDirectory(grandchild);

        Assert.Equal(childIdentity, adapter.InspectHandle(resolvedChild).Identity);
        using var parentOfChild = adapter.OpenParentDirectory(child);
        Assert.Equal(rootIdentity, adapter.InspectHandle(parentOfChild).Identity);

        child.Dispose();
        root.Dispose();

        using var resolvedChildAfterSourceDisposal = adapter.OpenParentDirectory(grandchild);
        Assert.Equal(childIdentity, adapter.InspectHandle(resolvedChildAfterSourceDisposal).Identity);
        using var resolvedRootAfterSourceDisposal = adapter.OpenParentDirectory(resolvedChildAfterSourceDisposal);
        Assert.Equal(rootIdentity, adapter.InspectHandle(resolvedRootAfterSourceDisposal).Identity);
    }

    [SupportedWindowsFact]
    public void ParentAncestry_WithoutDeleteSharing_RefusesAConcurrentRename()
    {
        using var fixture = new PackageStoreFixture();
        var adapter = new WindowsPhysicalStoreFileSystem();
        using var root = OpenFixtureRoot(adapter, fixture);
        using var child = adapter.CreateDirectoryExclusiveAt(root, "held-directory");
        var original = adapter.InspectHandle(child).Identity;

        var rename = Record.Exception(() => Directory.Move(fixture.GetPath("held-directory"), fixture.GetPath("moved-directory")));

        Assert.IsAssignableFrom<IOException>(rename);
        using var parent = adapter.OpenParentDirectory(child);
        Assert.Equal(original, adapter.InspectHandle(child).Identity);
        Assert.Equal(adapter.InspectHandle(root).Identity, adapter.InspectHandle(parent).Identity);
    }

    [SupportedWindowsFact]
    public void ReparseEntries_AreInspectedAndReadWithoutFollowingSymlinkOrJunction()
    {
        using var fixture = new PackageStoreFixture();
        var adapter = new WindowsPhysicalStoreFileSystem();
        using var root = OpenFixtureRoot(adapter, fixture);
        using var target = adapter.CreateDirectoryExclusiveAt(root, "target");
        var targetPath = fixture.GetPath("target");
        var relativeLinkPath = Path.Combine(fixture.RootPath, "relative-link");
        var junctionPath = Path.Combine(fixture.RootPath, "junction");
        Directory.CreateSymbolicLink(relativeLinkPath, "target");
        CreateJunction(junctionPath, targetPath);

        var relativeInfo = adapter.InspectChildNoFollow(root, "relative-link");
        var junctionInfo = adapter.InspectChildNoFollow(root, "junction");
        Assert.NotNull(relativeInfo);
        Assert.NotNull(junctionInfo);
        Assert.Equal(PhysicalStoreEntryKind.SymbolicLink, relativeInfo.Kind);
        Assert.Equal(PhysicalStoreEntryKind.SymbolicLink, junctionInfo.Kind);
        Assert.EndsWith("target", adapter.ReadLinkTargetNoFollow(root, "relative-link", relativeInfo.Identity), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target", adapter.ReadLinkTargetNoFollow(root, "junction", junctionInfo.Identity), StringComparison.OrdinalIgnoreCase);

        var wrongIdentity = new PhysicalFileIdentity(
            relativeInfo.Identity.Provider,
            relativeInfo.Identity.VolumeOrDeviceId,
            relativeInfo.Identity.FileId + "-different");
        var wrongTarget = Assert.Throws<PackageStoreAdmissionException>(
            () => adapter.ReadLinkTargetNoFollow(root, "relative-link", wrongIdentity));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, wrongTarget.Reason);
        Assert.Equal(
            PackageStoreAdmissionReason.UnknownAuthority,
            Assert.Throws<PackageStoreAdmissionException>(() => adapter.OpenDirectoryChildNoFollow(root, "relative-link")).Reason);
        Assert.Equal(
            PackageStoreAdmissionReason.UnknownAuthority,
            Assert.Throws<PackageStoreAdmissionException>(() => adapter.OpenDirectoryChildNoFollow(root, "junction")).Reason);
    }

    [SupportedWindowsFact]
    public void ControlFiles_AreExclusiveBoundedSingleLinkAndWriteOnce()
    {
        using var fixture = new PackageStoreFixture();
        var adapter = new WindowsPhysicalStoreFileSystem();
        using var root = OpenFixtureRoot(adapter, fixture);
        var contents = "windows-control-data"u8.ToArray();
        using var created = adapter.CreateFileExclusiveAt(root, "control.json");

        adapter.WriteNewControlFile(created, contents);

        Assert.Equal(contents, adapter.ReadControlFile(created, contents.Length));
        Assert.Throws<InvalidOperationException>(() => adapter.WriteNewControlFile(created, "second"u8.ToArray()));
        Assert.Equal(
            PackageStoreAdmissionReason.UnknownAuthority,
            Assert.Throws<PackageStoreAdmissionException>(() => adapter.CreateFileExclusiveAt(root, "control.json")).Reason);
        Assert.Equal(
            PackageStoreAdmissionReason.UnknownAuthority,
            Assert.Throws<PackageStoreAdmissionException>(() => adapter.ReadControlFile(created, contents.Length - 1)).Reason);

        created.Dispose();
        var hardLinkPath = fixture.GetPath("control-hardlink.json");
        Assert.True(CreateHardLinkW(hardLinkPath, fixture.GetPath("control.json"), IntPtr.Zero), $"CreateHardLinkW failed with {Marshal.GetLastPInvokeError()}.");
        var linked = adapter.InspectChildNoFollow(root, "control-hardlink.json");
        Assert.NotNull(linked);
        Assert.Equal(2UL, linked.LinkCount);
        Assert.Equal(
            PackageStoreAdmissionReason.UnknownAuthority,
            Assert.Throws<PackageStoreAdmissionException>(() => adapter.OpenFileChildNoFollow(root, "control-hardlink.json", FileAccess.Read)).Reason);
    }

    [SupportedWindowsFact]
    public async Task ExclusiveLock_IsNonblockingAndRemainsHeldAfterSourceWrapperDisposal()
    {
        using var fixture = new PackageStoreFixture();
        var adapter = new WindowsPhysicalStoreFileSystem();
        using var root = OpenFixtureRoot(adapter, fixture);
        using var source = adapter.CreateFileExclusiveAt(root, "store.lock");
        using var contender = adapter.OpenFileChildNoFollow(root, "store.lock", FileAccess.ReadWrite);
        using var afterReleaseProbe = adapter.OpenFileChildNoFollow(root, "store.lock", FileAccess.ReadWrite);

        await using var owner = Assert.IsAssignableFrom<IAsyncDisposable>(await adapter.TryAcquireExclusiveLock(source));
        Assert.Null(await adapter.TryAcquireExclusiveLock(source));
        Assert.Null(await adapter.TryAcquireExclusiveLock(contender));

        source.Dispose();
        Assert.Null(await adapter.TryAcquireExclusiveLock(afterReleaseProbe));

        await owner.DisposeAsync();
        await using var afterRelease = Assert.IsAssignableFrom<IAsyncDisposable>(await adapter.TryAcquireExclusiveLock(afterReleaseProbe));
    }

    [SupportedWindowsFact]
    public void Handles_RefuseForeignProviderAndExpiredScope()
    {
        using var fixture = new PackageStoreFixture();
        var adapter = new WindowsPhysicalStoreFileSystem();
        using var root = OpenFixtureRoot(adapter, fixture);
        var file = adapter.CreateFileExclusiveAt(root, "owned-control");
        var foreignAdapter = new WindowsPhysicalStoreFileSystem();

        var foreign = Assert.Throws<PackageStoreAdmissionException>(() => foreignAdapter.InspectHandle(file));
        Assert.Equal(PackageStoreAdmissionReason.RootMismatch, foreign.Reason);

        file.Dispose();
        var expired = Assert.Throws<PackageStoreAdmissionException>(() => adapter.InspectHandle(file));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, expired.Reason);
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
                var child = adapter.OpenDirectoryChildNoFollow(current, component);
                current.Dispose();
                current = child;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static void CreateJunction(string junctionPath, string targetPath)
    {
        Directory.CreateDirectory(junctionPath);
        using var handle = CreateFileW(
            junctionPath,
            0x40000000, // GENERIC_WRITE for FSCTL_SET_REPARSE_POINT.
            0x00000001 | 0x00000002 | 0x00000004,
            IntPtr.Zero,
            3,
            0x02000000 | 0x00200000,
            IntPtr.Zero);
        Assert.False(handle.IsInvalid, $"CreateFileW for the junction failed with {Marshal.GetLastPInvokeError()}.");

        var substituteName = "\\??\\" + Path.GetFullPath(targetPath).TrimEnd('\\');
        var printName = Path.GetFullPath(targetPath).TrimEnd('\\');
        var substituteBytes = Encoding.Unicode.GetBytes(substituteName);
        var printBytes = Encoding.Unicode.GetBytes(printName);
        var pathBufferLength = substituteBytes.Length + sizeof(char) + printBytes.Length + sizeof(char);
        var dataLength = 8 + pathBufferLength;
        var buffer = new byte[8 + dataLength];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), WindowsNative.ReparseTagMountPoint);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4, 2), checked((ushort)dataLength));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), checked((ushort)substituteBytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12, 2), checked((ushort)(substituteBytes.Length + sizeof(char))));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14, 2), checked((ushort)printBytes.Length));
        substituteBytes.CopyTo(buffer, 16);
        printBytes.CopyTo(buffer, 16 + substituteBytes.Length + sizeof(char));

        Assert.True(DeviceIoControl(
            handle,
            0x000900A4, // FSCTL_SET_REPARSE_POINT.
            buffer,
            checked((uint)buffer.Length),
            IntPtr.Zero,
            0,
            out _,
            IntPtr.Zero), $"FSCTL_SET_REPARSE_POINT failed with {Marshal.GetLastPInvokeError()}.");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[] inputBuffer,
        uint inputBufferSize,
        IntPtr outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}

public sealed class WindowsReparseTargetParserTests
{
    [Fact]
    public void ParseReparseTarget_RefusesTruncatedOffsetsAndInvalidUtf16()
    {
        var truncatedOffsets = CreateSymbolicLinkPayload([], offset: 0, length: 2, flags: 1);
        var oddUtf16Offset = CreateSymbolicLinkPayload([0x61, 0x00, 0x62, 0x00], offset: 1, length: 2, flags: 1);
        var invalidUtf16 = CreateSymbolicLinkPayload([0x00, 0xD8, 0x00, 0x00], offset: 0, length: 2, flags: 1);

        AssertUnknown(truncatedOffsets);
        AssertUnknown(oddUtf16Offset);
        AssertUnknown(invalidUtf16);
    }

    [Fact]
    public void ParseReparseTarget_RefusesUnknownTagsAndMismatchedRelativeFlags()
    {
        var unknownTag = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(unknownTag, 0xA0000100);
        Assert.Equal(
            PackageStoreAdmissionReason.UnsupportedFilesystem,
            Assert.Throws<PackageStoreAdmissionException>(() => WindowsPhysicalStoreFileSystem.ParseReparseTarget(unknownTag)).Reason);

        const string absolute = "\\??\\C:\\root\\target";
        const string relative = "target";
        AssertUnknown(CreateSymbolicLinkPayload(
            Encoding.Unicode.GetBytes(absolute), 0, checked((ushort)Encoding.Unicode.GetByteCount(absolute)), flags: 1));
        AssertUnknown(CreateSymbolicLinkPayload(
            Encoding.Unicode.GetBytes(relative), 0, checked((ushort)Encoding.Unicode.GetByteCount(relative)), flags: 0));
        AssertUnknown(CreateMountPointPayload(
            Encoding.Unicode.GetBytes(relative), 0, checked((ushort)Encoding.Unicode.GetByteCount(relative))));
    }

    [Fact]
    public void ParseReparseTarget_PreservesValidatedRelativeAndLocalAbsoluteForms()
    {
        const string relative = "..\\target";
        const string absolute = "\\??\\C:\\root\\target";

        Assert.Equal(relative, WindowsPhysicalStoreFileSystem.ParseReparseTarget(CreateSymbolicLinkPayload(
            Encoding.Unicode.GetBytes(relative), 0, checked((ushort)Encoding.Unicode.GetByteCount(relative)), flags: 1)));
        Assert.Equal(absolute, WindowsPhysicalStoreFileSystem.ParseReparseTarget(CreateSymbolicLinkPayload(
            Encoding.Unicode.GetBytes(absolute), 0, checked((ushort)Encoding.Unicode.GetByteCount(absolute)), flags: 0)));
    }

    private static byte[] CreateSymbolicLinkPayload(byte[] pathBuffer, ushort offset, ushort length, uint flags)
    {
        var dataLength = checked(12 + pathBuffer.Length);
        var data = new byte[8 + dataLength];
        BinaryPrimitives.WriteUInt32LittleEndian(data, WindowsNative.ReparseTagSymbolicLink);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), checked((ushort)dataLength));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), offset);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), flags);
        pathBuffer.CopyTo(data, 20);
        return data;
    }

    private static byte[] CreateMountPointPayload(byte[] pathBuffer, ushort offset, ushort length)
    {
        var dataLength = checked(8 + pathBuffer.Length);
        var data = new byte[8 + dataLength];
        BinaryPrimitives.WriteUInt32LittleEndian(data, WindowsNative.ReparseTagMountPoint);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), checked((ushort)dataLength));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), offset);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), length);
        pathBuffer.CopyTo(data, 16);
        return data;
    }

    private static void AssertUnknown(byte[] data)
    {
        var error = Assert.Throws<PackageStoreAdmissionException>(() => WindowsPhysicalStoreFileSystem.ParseReparseTarget(data));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
    }
}

public sealed class SupportedWindowsFactAttribute : FactAttribute
{
    public SupportedWindowsFactAttribute()
    {
        if (!WindowsPhysicalStoreFileSystem.IsSupportedPlatform)
            Skip = "Native Windows store operations are qualified only on Windows x64; this skip is not runtime acceptance.";
    }
}
