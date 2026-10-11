using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Windows")]
public sealed class WindowsPhysicalStoreIdentityTests
{
    [SupportedWindowsFact]
    public void StateSlotObservation_UsesStoredCaseAndNativeCaseInsensitiveProfile()
    {
        using var fixture = new PackageStoreFixture();
        var adapter = new WindowsPhysicalStoreFileSystem();
        using var root = OpenFixtureRoot(adapter, fixture);
        using var created = adapter.CreateFileExclusiveAt(root, "State.JSON");
        adapter.WriteNewControlFile(created, "state"u8.ToArray());
        var identity = new PhysicalStoreIdentity(adapter);

        var original = identity.ObserveStateSlot(root, "State.JSON");
        var caseVariant = identity.ObserveStateSlot(root, "sTATE.json");

        Assert.Equal(original.Slot, caseVariant.Slot);
        Assert.Equal(original.FileIdentity, caseVariant.FileIdentity);
        Assert.Equal("State.JSON", original.Slot.CanonicalBasename);
        Assert.Equal("windows-ntfs-name-v1", original.Slot.NameSemantics.ProfileId);
        Assert.Equal(PhysicalStoreNameEncoding.Utf16LittleEndian, original.Slot.NameSemantics.Encoding);
        Assert.False(original.Slot.NameSemantics.CaseSensitive);
        Assert.False(original.Slot.NameSemantics.NormalizationInsensitive);
    }

    [SupportedWindowsFact]
    public void StateSlotObservation_CaseSensitiveDirectoryKeepsCaseVariantsDistinct()
    {
        using var fixture = new PackageStoreFixture();
        var adapter = new WindowsPhysicalStoreFileSystem();
        using var root = OpenFixtureRoot(adapter, fixture);
        var directoryPath = fixture.GetPath("case-sensitive");
        Directory.CreateDirectory(directoryPath);
        SetDirectoryCaseSensitive(directoryPath);
        using var caseSensitive = adapter.OpenDirectoryChildNoFollow(root, "case-sensitive");
        using var upper = adapter.CreateFileExclusiveAt(caseSensitive, "State.JSON");
        using var lower = adapter.CreateFileExclusiveAt(caseSensitive, "state.json");
        adapter.WriteNewControlFile(upper, "upper"u8.ToArray());
        adapter.WriteNewControlFile(lower, "lower"u8.ToArray());
        var identity = new PhysicalStoreIdentity(adapter);

        var upperObservation = identity.ObserveStateSlot(caseSensitive, "State.JSON");
        var lowerObservation = identity.ObserveStateSlot(caseSensitive, "state.json");

        Assert.True(upperObservation.Slot.NameSemantics.CaseSensitive);
        Assert.NotEqual(upperObservation.Slot.CanonicalBasename, lowerObservation.Slot.CanonicalBasename);
        Assert.NotEqual(upperObservation.Slot, lowerObservation.Slot);
        Assert.NotEqual(upperObservation.FileIdentity, lowerObservation.FileIdentity);
    }

    [WindowsShortNameFact]
    public void StateSlotObservation_8Dot3AliasResolvesToTheStoredLongBasenameWhenEnabled()
    {
        using var fixture = new PackageStoreFixture();
        var adapter = new WindowsPhysicalStoreFileSystem();
        using var root = OpenFixtureRoot(adapter, fixture);
        const string longName = "canonical-state-name-2026.json";
        using var created = adapter.CreateFileExclusiveAt(root, longName);
        adapter.WriteNewControlFile(created, "state"u8.ToArray());

        var shortPath = GetShortPathName(fixture.GetPath(longName));
        var shortName = Path.GetFileName(shortPath);
        if (string.Equals(shortName, longName, StringComparison.OrdinalIgnoreCase))
            Assert.Fail("The discovery-time 8.3 capability probe succeeded, but the owned test file did not receive a short alias.");

        var observation = new PhysicalStoreIdentity(adapter).ObserveStateSlot(root, shortName);

        Assert.Equal(longName, observation.Slot.CanonicalBasename);
        Assert.Equal(adapter.InspectHandle(created).Identity, observation.FileIdentity);
    }

    [SupportedWindowsFact]
    public void StateSlotObservation_RefusesWrongIdentityReparseAndHardLinkEntries()
    {
        using var fixture = new PackageStoreFixture();
        var adapter = new WindowsPhysicalStoreFileSystem();
        using var root = OpenFixtureRoot(adapter, fixture);
        var statePath = fixture.GetPath("state.json");
        var created = adapter.CreateFileExclusiveAt(root, "state.json");
        adapter.WriteNewControlFile(created, "state"u8.ToArray());
        var actualIdentity = adapter.InspectHandle(created).Identity;
        created.Dispose();

        var wrongIdentity = new PhysicalFileIdentity(
            actualIdentity.Provider,
            actualIdentity.VolumeOrDeviceId,
            actualIdentity.FileId + "-wrong");
        var wrong = Assert.Throws<PackageStoreAdmissionException>(() => adapter.ObserveCanonicalFileNameNoFollow(root, "state.json", wrongIdentity));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, wrong.Reason);

        File.CreateSymbolicLink(fixture.GetPath("state-link"), statePath);
        var link = Assert.Throws<PackageStoreAdmissionException>(() => new PhysicalStoreIdentity(adapter).ObserveStateSlot(root, "state-link"));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, link.Reason);

        Assert.True(CreateHardLinkW(fixture.GetPath("state-hardlink"), statePath, IntPtr.Zero), $"CreateHardLinkW failed with {Marshal.GetLastPInvokeError()}.");
        var hardLink = Assert.Throws<PackageStoreAdmissionException>(() => new PhysicalStoreIdentity(adapter).ObserveStateSlot(root, "state-hardlink"));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, hardLink.Reason);
    }

    [SupportedWindowsFact]
    public void CanonicalNameObservation_RequiresTheOwnedParentHandleToRemainOpen()
    {
        using var fixture = new PackageStoreFixture();
        var adapter = new WindowsPhysicalStoreFileSystem();
        using var root = OpenFixtureRoot(adapter, fixture);
        File.WriteAllText(fixture.GetPath("state.json"), "state");
        root.Dispose();

        var error = Assert.Throws<PackageStoreAdmissionException>(
            () => adapter.ObserveCanonicalFileNameNoFollow(root, "state.json", new PhysicalFileIdentity("windows-ntfs-x64", "wrong", "wrong")));

        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, error.Reason);
    }

    [SupportedWindowsFact]
    public void StateSlotObservation_AtomicReplacementKeepsSlotButChangesFileIdentityAndRenameChangesSlot()
    {
        using var fixture = new PackageStoreFixture();
        var adapter = new WindowsPhysicalStoreFileSystem();
        using var root = OpenFixtureRoot(adapter, fixture);
        var statePath = fixture.GetPath("state.json");
        File.WriteAllText(statePath, "initial");
        var identity = new PhysicalStoreIdentity(adapter);
        var initial = identity.ObserveStateSlot(root, "state.json");

        using (var replacement = adapter.CreateFileExclusiveAt(root, "replacement.tmp"))
            adapter.WriteNewControlFile(replacement, "replacement"u8.ToArray());
        File.Replace(fixture.GetPath("replacement.tmp"), statePath, destinationBackupFileName: null);
        var replaced = identity.ObserveStateSlot(root, "state.json");

        Assert.Equal(initial.Slot, replaced.Slot);
        Assert.NotEqual(initial.FileIdentity, replaced.FileIdentity);

        var renamedPath = fixture.GetPath("renamed-state.json");
        File.Move(statePath, renamedPath);
        var renamed = identity.ObserveStateSlot(root, "renamed-state.json");

        Assert.NotEqual(replaced.Slot, renamed.Slot);
        Assert.NotEqual(replaced.Slot.CanonicalBasename, renamed.Slot.CanonicalBasename);
        Assert.Equal(replaced.FileIdentity, renamed.FileIdentity);
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

    private static string GetShortPathName(string path)
    {
        var buffer = new StringBuilder(32768);
        var length = GetShortPathNameW(path, buffer, checked((uint)buffer.Capacity));
        Assert.True(length != 0, $"GetShortPathNameW failed with {Marshal.GetLastPInvokeError()}.");
        Assert.True(length < buffer.Capacity, "The short path exceeded the supported test buffer.");
        return buffer.ToString();
    }

    internal static bool Has8Dot3AliasCapability()
    {
        var probeDirectory = Path.Combine(Path.GetTempPath(), $"nuplane-short-name-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(probeDirectory);
        var longName = $"long-name-probe-{Guid.NewGuid():N}.txt";
        var path = Path.Combine(probeDirectory, longName);
        try
        {
            File.WriteAllText(path, string.Empty);
            var buffer = new StringBuilder(32768);
            var length = GetShortPathNameW(path, buffer, checked((uint)buffer.Capacity));
            if (length == 0 || length >= buffer.Capacity)
                throw new IOException($"GetShortPathNameW capability probe failed with {Marshal.GetLastPInvokeError()}.");
            return !string.Equals(Path.GetFileName(buffer.ToString()), longName, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            if (Directory.Exists(probeDirectory))
                Directory.Delete(probeDirectory);
        }
    }

    private static void SetDirectoryCaseSensitive(string path)
    {
        using var handle = CreateFileW(
            path,
            0x00000100, // FILE_WRITE_ATTRIBUTES.
            0x00000001 | 0x00000002 | 0x00000004,
            IntPtr.Zero,
            3,
            0x02000000 | 0x00200000,
            IntPtr.Zero);
        Assert.False(handle.IsInvalid, $"CreateFileW for the case-sensitive directory failed with {Marshal.GetLastPInvokeError()}.");
        var info = new FileCaseSensitiveInformation { Flags = 1 };
        Assert.True(SetFileInformationByHandle(handle, 23, ref info, checked((uint)Marshal.SizeOf<FileCaseSensitiveInformation>())),
            $"SetFileInformationByHandle(FileCaseSensitiveInfo) failed with {Marshal.GetLastPInvokeError()}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileCaseSensitiveInformation
    {
        internal uint Flags;
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

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint bufferLength);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileCaseSensitiveInformation fileInformation,
        uint bufferSize);
}

public sealed class WindowsShortNameFactAttribute : FactAttribute
{
    public WindowsShortNameFactAttribute()
    {
        if (!WindowsPhysicalStoreFileSystem.IsSupportedPlatform)
            Skip = "Native Windows store operations are qualified only on Windows x64; this skip is not runtime acceptance.";
        else if (!WindowsPhysicalStoreIdentityTests.Has8Dot3AliasCapability())
            Skip = "The test volume does not create 8.3 aliases; this alias-specific scenario is not acceptance evidence.";
    }
}
