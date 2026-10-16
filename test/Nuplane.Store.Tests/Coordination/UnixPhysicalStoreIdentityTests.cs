using System.Runtime.InteropServices;
using System.Text;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Unix;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Unix")]
public sealed class UnixPhysicalStoreIdentityTests
{
    [SupportedUnixFact]
    public void ObserveStateSlot_UsesFilesystemSpellingAndNameProfileForAliases()
    {
        using var fixture = new PackageStoreFixture();
        var adapter = new UnixPhysicalStoreFileSystem();
        _ = fixture.CreateDirectory("state-parent");
        using var parent = OpenDirectoryPath(adapter, fixture.GetPath("state-parent"));
        using var state = adapter.CreateFileExclusiveAt(parent, "MiXeD-Normalized-State.dat");
        var identity = new PhysicalStoreIdentity(adapter);
        var original = identity.ObserveStateSlot(parent, "MiXeD-Normalized-State.dat");
        var profile = original.Slot.NameSemantics;

        Assert.Equal("MiXeD-Normalized-State.dat", original.Slot.CanonicalBasename);
        Assert.Equal(adapter.InspectHandle(parent).Identity, original.Slot.ParentIdentity);
        Assert.Equal(adapter.InspectHandle(state).Identity, original.FileIdentity);
        Assert.Equal(PhysicalStoreNameEncoding.Utf8, profile.Encoding);

        const string alternateCase = "mIxEd-nORMALIZED-sTATE.DAT";
        if (profile.CaseSensitive)
        {
            using var distinctCase = adapter.CreateFileExclusiveAt(parent, alternateCase);
            var distinct = identity.ObserveStateSlot(parent, alternateCase);
            Assert.NotEqual(original.Slot, distinct.Slot);
            Assert.Equal(alternateCase, distinct.Slot.CanonicalBasename);
        }
        else
        {
            var caseAlias = identity.ObserveStateSlot(parent, alternateCase);
            Assert.Equal(original.Slot, caseAlias.Slot);
            Assert.Equal(original.FileIdentity, caseAlias.FileIdentity);
        }

        const string composed = "state-\u00e9.dat";
        const string decomposed = "state-e\u0301.dat";
        using var composedState = adapter.CreateFileExclusiveAt(parent, composed);
        var composedObservation = identity.ObserveStateSlot(parent, composed);
        if (profile.NormalizationInsensitive)
        {
            var normalizedAlias = identity.ObserveStateSlot(parent, decomposed);
            Assert.Equal(composedObservation.Slot, normalizedAlias.Slot);
            Assert.Equal(composedObservation.FileIdentity, normalizedAlias.FileIdentity);
        }
        else
        {
            using var decomposedState = adapter.CreateFileExclusiveAt(parent, decomposed);
            var distinctNormalization = identity.ObserveStateSlot(parent, decomposed);
            Assert.NotEqual(composedObservation.Slot, distinctNormalization.Slot);
            Assert.Equal(decomposed, distinctNormalization.Slot.CanonicalBasename);
        }
    }

    [LinuxExt4CasefoldFact]
    public void ObserveStateSlot_Ext4CasefoldResolvesCaseAndNormalizationAliases()
    {
        using var fixture = new PackageStoreFixture();
        var adapter = new UnixPhysicalStoreFileSystem();
        var parentPath = fixture.CreateDirectory("casefold-parent");
        EnableExt4Casefold(parentPath);

        using var parent = OpenDirectoryPath(adapter, parentPath);
        const string storedName = "State-MiXeD-\u00e9.json";
        const string aliasName = "sTATE-mIxEd-e\u0301.JSON";
        using var state = adapter.CreateFileExclusiveAt(parent, storedName);

        var identity = new PhysicalStoreIdentity(adapter);
        var stored = identity.ObserveStateSlot(parent, storedName);
        var alias = identity.ObserveStateSlot(parent, aliasName);

        Assert.Equal("linux-ext4-casefold-v1", stored.Slot.NameSemantics.ProfileId);
        Assert.False(stored.Slot.NameSemantics.CaseSensitive);
        Assert.True(stored.Slot.NameSemantics.NormalizationInsensitive);
        Assert.Equal(stored.Slot, alias.Slot);
        Assert.Equal(stored.FileIdentity, alias.FileIdentity);
        Assert.Equal(storedName, alias.Slot.CanonicalBasename);
    }

    [SupportedUnixFact]
    public void ObserveStateSlot_ParentAliasesConvergeAndAtomicReplacePreservesSlot()
    {
        using var fixture = new PackageStoreFixture();
        var adapter = new UnixPhysicalStoreFileSystem();
        var parentPath = fixture.CreateDirectory("physical-parent");
        var aliasPath = fixture.GetPath("parent-alias");
        using (var root = OpenDirectoryPath(adapter, fixture.RootPath))
        using (var physicalParent = adapter.OpenDirectoryChildNoFollow(root, "physical-parent"))
        using (var state = adapter.CreateFileExclusiveAt(physicalParent, "state.json"))
        {
            Directory.CreateSymbolicLink(aliasPath, parentPath);
        }

        using var fromPhysicalPath = OpenDirectoryPath(adapter, fixture.GetPath("physical-parent"));
        using var fromAliasPath = OpenDirectoryPath(adapter, aliasPath);
        var identity = new PhysicalStoreIdentity(adapter);
        var original = identity.ObserveStateSlot(fromPhysicalPath, "state.json");
        var aliasParent = identity.ObserveStateSlot(fromAliasPath, "state.json");
        Assert.Equal(original.Slot, aliasParent.Slot);
        Assert.Equal(original.FileIdentity, aliasParent.FileIdentity);

        var replacementPath = fixture.GetPath("replacement.tmp");
        File.WriteAllText(replacementPath, "replacement");
        File.Move(replacementPath, fixture.GetPath("physical-parent/state.json"), overwrite: true);

        var afterReplacement = identity.ObserveStateSlot(fromPhysicalPath, "state.json");
        Assert.Equal(original.Slot, afterReplacement.Slot);
        Assert.NotEqual(original.FileIdentity, afterReplacement.FileIdentity);
    }

    [SupportedUnixFact]
    public void ObserveCanonicalFileNameNoFollow_RejectsWrongIdentityLinksAndForeignHandles()
    {
        using var fixture = new PackageStoreFixture();
        var adapter = new UnixPhysicalStoreFileSystem();
        _ = fixture.CreateDirectory("state-parent");
        using var parent = OpenDirectoryPath(adapter, fixture.GetPath("state-parent"));
        using var state = adapter.CreateFileExclusiveAt(parent, "state.json");
        var stateIdentity = adapter.InspectHandle(state).Identity;
        var wrongIdentity = new PhysicalFileIdentity(
            stateIdentity.Provider,
            stateIdentity.VolumeOrDeviceId,
            stateIdentity.FileId + "-wrong");

        var wrong = Assert.Throws<PackageStoreAdmissionException>(
            () => adapter.ObserveCanonicalFileNameNoFollow(parent, "state.json", wrongIdentity));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, wrong.Reason);

        File.CreateSymbolicLink(fixture.GetPath("state-parent/state-link"), "state.json");
        var linked = Assert.Throws<PackageStoreAdmissionException>(
            () => adapter.ObserveCanonicalFileNameNoFollow(parent, "state-link", stateIdentity));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, linked.Reason);

        CreateHardLink(fixture.GetPath("state-parent/state.json"), fixture.GetPath("state-parent/state-hardlink"));
        var hardlinked = Assert.Throws<PackageStoreAdmissionException>(
            () => adapter.ObserveCanonicalFileNameNoFollow(parent, "state-hardlink", stateIdentity));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, hardlinked.Reason);

        var foreignAdapter = new UnixPhysicalStoreFileSystem();
        var foreign = Assert.Throws<PackageStoreAdmissionException>(
            () => foreignAdapter.ObserveCanonicalFileNameNoFollow(parent, "state.json", stateIdentity));
        Assert.Equal(PackageStoreAdmissionReason.RootMismatch, foreign.Reason);

        parent.Dispose();
        var disposed = Assert.Throws<PackageStoreAdmissionException>(
            () => adapter.ObserveCanonicalFileNameNoFollow(parent, "state.json", stateIdentity));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, disposed.Reason);
    }

    private static PhysicalStoreDirectoryHandle OpenDirectoryPath(
        UnixPhysicalStoreFileSystem adapter,
        string path)
    {
        var resolved = ResolveRealPath(path);
        var current = adapter.OpenNamespaceRoot("/");
        try
        {
            foreach (var segment in resolved.Split('/', StringSplitOptions.RemoveEmptyEntries))
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
            throw new IOException($"The owned state-identity fixture path could not be resolved (native error {Marshal.GetLastPInvokeError()}).");

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

    internal static void EnableExt4Casefold(string directoryPath)
    {
        const ulong LinuxGetFlagsRequest = 0x80086601;
        const ulong LinuxSetFlagsRequest = 0x40086602;
        const uint LinuxCasefoldFlag = 0x40000000;

        Assert.Empty(Directory.EnumerateFileSystemEntries(directoryPath));

        var fileDescriptor = LinuxNative.OpenDirectory(
            directoryPath,
            UnixNative.LinuxDirectoryStreamFlags);
        if (fileDescriptor < 0)
            throw new IOException($"Could not open the owned ext4 casefold directory (native error {Marshal.GetLastPInvokeError()}).");

        try
        {
            if (UnixNamesNative.LinuxFStatFs(fileDescriptor, out var fileSystem) != 0)
                throw new IOException($"Could not identify the owned casefold directory filesystem (native error {Marshal.GetLastPInvokeError()}).");
            Assert.Equal(0xEF53L, fileSystem.FileSystemType);

            long flags = 0;
            if (LinuxNative.Ioctl(fileDescriptor, LinuxGetFlagsRequest, ref flags) != 0)
                throw new IOException($"Could not read ext4 directory flags (native error {Marshal.GetLastPInvokeError()}).");

            flags = (uint)flags | LinuxCasefoldFlag;
            if (LinuxNative.Ioctl(fileDescriptor, LinuxSetFlagsRequest, ref flags) != 0)
                throw new IOException($"Could not enable ext4 casefold on the empty owned directory (native error {Marshal.GetLastPInvokeError()}).");

            long verifiedFlags = 0;
            if (LinuxNative.Ioctl(fileDescriptor, LinuxGetFlagsRequest, ref verifiedFlags) != 0 ||
                (((uint)verifiedFlags) & LinuxCasefoldFlag) == 0)
            {
                throw new IOException($"The ext4 directory did not retain its casefold flag (native error {Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            _ = LinuxNative.Close(fileDescriptor);
        }
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

    private static class LinuxNative
    {
        [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int OpenDirectory(string path, int flags);

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        internal static extern int Ioctl(int fileDescriptor, ulong request, ref long value);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        internal static extern int Close(int fileDescriptor);
    }
}

public sealed class LinuxExt4CasefoldFactAttribute : FactAttribute
{
    public LinuxExt4CasefoldFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || !UnixPhysicalStoreFileSystem.IsSupportedPlatform)
            Skip = "Ext4 casefold qualification requires a supported Linux x64/arm64 runner.";
        else if (!string.Equals(Environment.GetEnvironmentVariable("NUPLANE_REQUIRE_EXT4_CASEFOLD"), "1", StringComparison.Ordinal))
            Skip = "Set NUPLANE_REQUIRE_EXT4_CASEFOLD=1 on the dedicated ext4 casefold test lane.";
    }
}
