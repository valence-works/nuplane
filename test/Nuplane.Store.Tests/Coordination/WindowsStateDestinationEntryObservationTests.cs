using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Windows;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Windows")]
public sealed class WindowsStateDestinationEntryObservationTests
{
    [Fact]
    public void ParseStateDestinationDirectoryEntry_PreservesFileIdAndBothNames()
    {
        var result = WindowsNative.ParseStateDestinationDirectoryEntry(
            CreateRecord("Stored-State.json", "STORE~1.JSN", fileIdLow: 0x1122334455667788, fileIdHigh: 0x99AABBCCDDEEFF00));

        Assert.Equal(0x1122334455667788UL, result.FileIdLow);
        Assert.Equal(0x99AABBCCDDEEFF00UL, result.FileIdHigh);
        Assert.Equal("Stored-State.json", result.FileName);
        Assert.Equal("STORE~1.JSN", result.ShortName);
        Assert.Equal(WindowsNative.FileAttributeNormal, result.FileAttributes);
        Assert.Equal(0u, result.ReparsePointTag);
    }

    [Fact]
    public void ParseStateDestinationDirectoryEntry_RejectsMalformedOrAmbiguousRecordsAndWildcardFilters()
    {
        var valid = CreateRecord("state.json", string.Empty, fileIdLow: 1, fileIdHigh: 2);

        Assert.Throws<WindowsNativeCallException>(() => WindowsNative.ParseStateDestinationDirectoryEntry(valid[..113]));
        Assert.Throws<WindowsNativeCallException>(() => WindowsNative.ParseStateDestinationDirectoryEntry(Mutate(valid, 0, 8)));
        Assert.Throws<WindowsNativeCallException>(() => WindowsNative.ParseStateDestinationDirectoryEntry(MutateUInt32(valid, 60, 3)));
        Assert.Throws<WindowsNativeCallException>(() => WindowsNative.ParseStateDestinationDirectoryEntry(Mutate(valid, 88, 25)));
        Assert.Throws<WindowsNativeCallException>(() => WindowsNative.ParseStateDestinationDirectoryEntry(MutateUInt64(MutateUInt64(valid, 72, 0), 80, 0)));
        var invalidUtf16 = CreateRecord("x", string.Empty, 1, 2);
        invalidUtf16[114] = 0x00;
        invalidUtf16[115] = 0xD8;
        Assert.Throws<WindowsNativeCallException>(() => WindowsNative.ParseStateDestinationDirectoryEntry(invalidUtf16));
        Assert.Throws<WindowsNativeCallException>(() => WindowsNative.ParseStateDestinationDirectoryEntry(CreateRecord("../state", string.Empty, 1, 2)));
        Assert.Throws<WindowsNativeCallException>(() => WindowsNative.ParseStateDestinationDirectoryEntry(CreateRecord("state<name", string.Empty, 1, 2)));

        using var invalidHandle = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        Assert.Throws<WindowsNativeCallException>(() => WindowsNative.QueryStateDestinationDirectoryEntry(invalidHandle, "state<name.json"));
    }

    [SupportedWindowsFact]
    public void Observe_CaseInsensitiveVariantReturnsStoredLongName()
    {
        using var context = WindowsPhysicalStorePublicationTests.CreateContext();
        File.WriteAllText(Path.Combine(context.ParentPath, "Stored-State.json"), "state");

        var observation = Observe(context, "sTORED-state.JSON");

        Assert.Equal("sTORED-state.JSON", observation.RequestedName);
        Assert.Equal("Stored-State.json", observation.StoredName);
        Assert.Equal(context.FileSystem.InspectChildNoFollow(context.Parent, "Stored-State.json")!.Identity, observation.Entry.Identity);
        Assert.False(observation.NameSemantics.CaseSensitive);
    }

    [SupportedWindowsFact]
    public void Observe_CaseSensitiveVariantsReturnDistinctEntries()
    {
        using var context = WindowsPhysicalStorePublicationTests.CreateContext();
        var directoryPath = Path.Combine(context.ParentPath, "case-sensitive");
        Directory.CreateDirectory(directoryPath);
        WindowsPhysicalStoreIdentityTests.SetDirectoryCaseSensitive(directoryPath);
        using var directory = ((WindowsPhysicalStoreFileSystem)context.FileSystem).OpenDirectoryChildNoFollow(context.Parent, "case-sensitive");
        File.WriteAllText(Path.Combine(directoryPath, "State.JSON"), "upper");
        File.WriteAllText(Path.Combine(directoryPath, "state.json"), "lower");
        var adapter = (WindowsPhysicalStoreFileSystem)context.FileSystem;
        var parent = adapter.InspectHandle(directory);
        var semantics = adapter.ObserveDirectoryNameSemantics(directory);

        var upper = adapter.ObserveStateDestinationEntryNoFollow(directory, "State.JSON", parent.Identity, semantics);
        var lower = adapter.ObserveStateDestinationEntryNoFollow(directory, "state.json", parent.Identity, semantics);

        Assert.True(upper.NameSemantics.CaseSensitive);
        Assert.Equal("State.JSON", upper.StoredName);
        Assert.Equal("state.json", lower.StoredName);
        Assert.NotEqual(upper.Entry.Identity, lower.Entry.Identity);
    }

    [SupportedWindowsFact]
    public void Observe_CaseSensitiveCaseOnlyHardLinksReturnTheirExactNames()
    {
        using var context = WindowsPhysicalStorePublicationTests.CreateContext();
        var directoryPath = Path.Combine(context.ParentPath, "case-sensitive-hardlinks");
        Directory.CreateDirectory(directoryPath);
        WindowsPhysicalStoreIdentityTests.SetDirectoryCaseSensitive(directoryPath);
        using var directory = ((WindowsPhysicalStoreFileSystem)context.FileSystem).OpenDirectoryChildNoFollow(context.Parent, "case-sensitive-hardlinks");
        const string upperName = "State.json";
        const string lowerName = "state.json";
        File.WriteAllText(Path.Combine(directoryPath, upperName), "state");
        WindowsPhysicalStorePublicationTests.CreateHardLink(
            Path.Combine(directoryPath, upperName),
            Path.Combine(directoryPath, lowerName));

        var adapter = (WindowsPhysicalStoreFileSystem)context.FileSystem;
        var parent = adapter.InspectHandle(directory);
        var semantics = adapter.ObserveDirectoryNameSemantics(directory);
        Assert.True(semantics.CaseSensitive);

        var upper = adapter.ObserveStateDestinationEntryNoFollow(directory, upperName, parent.Identity, semantics);
        var lower = adapter.ObserveStateDestinationEntryNoFollow(directory, lowerName, parent.Identity, semantics);

        Assert.Equal(upperName, upper.StoredName);
        Assert.Equal(lowerName, lower.StoredName);
        Assert.Equal(upper.Entry.Identity, lower.Entry.Identity);
    }

    [WindowsShortNameFact]
    public void Observe_8Dot3AliasReturnsStoredLongAndShortNamesWhenEnabled()
    {
        using var context = WindowsPhysicalStorePublicationTests.CreateContext();
        const string longName = "ordinary-state-destination-name-2026.json";
        File.WriteAllText(Path.Combine(context.ParentPath, longName), "state");
        var shortName = Path.GetFileName(WindowsPhysicalStoreIdentityTests.GetShortPathName(Path.Combine(context.ParentPath, longName)));
        Assert.False(string.Equals(shortName, longName, StringComparison.OrdinalIgnoreCase));

        var observation = Observe(context, shortName);

        Assert.Equal(shortName, observation.RequestedName);
        Assert.Equal(longName, observation.StoredName);
        Assert.Equal(shortName, observation.ShortName, ignoreCase: true);
        Assert.Equal(context.FileSystem.InspectChildNoFollow(context.Parent, longName)!.Identity, observation.Entry.Identity);
    }

    [SupportedWindowsFact]
    public void Observe_SiblingHardLinksReturnDistinctNamesWithTheSameFullIdentity()
    {
        using var context = WindowsPhysicalStorePublicationTests.CreateContext();
        const string firstName = "state-first.json";
        const string secondName = "state-second.json";
        File.WriteAllText(Path.Combine(context.ParentPath, firstName), "state");
        WindowsPhysicalStorePublicationTests.CreateHardLink(
            Path.Combine(context.ParentPath, firstName),
            Path.Combine(context.ParentPath, secondName));

        var first = Observe(context, firstName);
        var second = Observe(context, secondName);

        Assert.Equal(firstName, first.StoredName);
        Assert.Equal(secondName, second.StoredName);
        Assert.Equal(first.Entry.Identity, second.Entry.Identity);
        Assert.Equal(2UL, first.Entry.LinkCount);
        Assert.Equal(2UL, second.Entry.LinkCount);
    }

    [SupportedWindowsFact]
    public void Observe_SymbolicLinksAndJunctionsReturnNoFollowReparseEntries()
    {
        using var context = WindowsPhysicalStorePublicationTests.CreateContext();
        var fileTarget = Path.Combine(context.ParentPath, "target.json");
        File.WriteAllText(fileTarget, "target-state");
        var fileLink = Path.Combine(context.ParentPath, "state-link.json");
        File.CreateSymbolicLink(fileLink, fileTarget);

        var directoryTarget = Path.Combine(context.ParentPath, "directory-target");
        Directory.CreateDirectory(directoryTarget);
        var junction = Path.Combine(context.ParentPath, "state-junction");
        WindowsPhysicalStoreFileSystemTests.CreateJunction(junction, directoryTarget);

        var symbolicLink = Observe(context, "state-link.json");
        var junctionEntry = Observe(context, "state-junction");

        Assert.Equal(PhysicalStoreEntryKind.SymbolicLink, symbolicLink.Entry.Kind);
        Assert.Equal(WindowsNative.ReparseTagSymbolicLink, symbolicLink.Entry.ReparseTag);
        Assert.NotEqual(context.FileSystem.InspectChildNoFollow(context.Parent, "target.json")!.Identity, symbolicLink.Entry.Identity);
        Assert.Equal(PhysicalStoreEntryKind.SymbolicLink, junctionEntry.Entry.Kind);
        Assert.Equal(WindowsNative.ReparseTagMountPoint, junctionEntry.Entry.ReparseTag);
        Assert.NotEqual(((WindowsPhysicalStoreFileSystem)context.FileSystem).InspectChildNoFollow(context.Parent, "directory-target")!.Identity,
            junctionEntry.Entry.Identity);
    }

    [SupportedWindowsFact]
    public void Observe_RefusesWrongParentIdentityForeignProviderAndDisposedHandle()
    {
        using var context = WindowsPhysicalStorePublicationTests.CreateContext();
        File.WriteAllText(Path.Combine(context.ParentPath, "state.json"), "state");
        var adapter = (WindowsPhysicalStoreFileSystem)context.FileSystem;
        var parent = adapter.InspectHandle(context.Parent);
        var semantics = adapter.ObserveDirectoryNameSemantics(context.Parent);
        var wrongIdentity = new PhysicalFileIdentity(
            parent.Identity.Provider,
            parent.Identity.VolumeOrDeviceId,
            parent.Identity.FileId + "-wrong");

        var wrongParent = Assert.Throws<PackageStoreAdmissionException>(() =>
            adapter.ObserveStateDestinationEntryNoFollow(context.Parent, "state.json", wrongIdentity, semantics));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, wrongParent.Reason);

        var foreign = new WindowsPhysicalStoreFileSystem();
        var foreignParent = Assert.Throws<PackageStoreAdmissionException>(() =>
            foreign.ObserveStateDestinationEntryNoFollow(context.Parent, "state.json", parent.Identity, semantics));
        Assert.Equal(PackageStoreAdmissionReason.RootMismatch, foreignParent.Reason);

        context.Parent.Dispose();
        var disposedParent = Assert.Throws<PackageStoreAdmissionException>(() =>
            adapter.ObserveStateDestinationEntryNoFollow(context.Parent, "state.json", parent.Identity, semantics));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, disposedParent.Reason);
    }

    private static WindowsStateDestinationEntryObservation Observe(
        PhysicalStorePublicationTestContext context,
        string requestedName)
    {
        var adapter = (WindowsPhysicalStoreFileSystem)context.FileSystem;
        var parent = adapter.InspectHandle(context.Parent);
        var semantics = adapter.ObserveDirectoryNameSemantics(context.Parent);
        return adapter.ObserveStateDestinationEntryNoFollow(context.Parent, requestedName, parent.Identity, semantics);
    }

    [Fact]
    public void RequireSelectedDirectoryRecord_CaseSensitiveQueryCannotSelectCaseOnlySibling()
    {
        var record = new WindowsNative.StateDestinationDirectoryEntry(
            FileIdLow: 1,
            FileIdHigh: 0,
            FileAttributes: WindowsNative.FileAttributeNormal,
            EndOfFile: 1,
            ReparsePointTag: 0,
            ShortName: string.Empty,
            FileName: "state.json");
        var semantics = new PhysicalStoreNameSemantics(
            "test-case-sensitive",
            PhysicalStoreNameEncoding.Utf16LittleEndian,
            caseSensitive: true,
            normalizationInsensitive: false);

        var refusal = Assert.Throws<PackageStoreAdmissionException>(() =>
            WindowsPhysicalStoreFileSystem.RequireSelectedDirectoryRecord(record, "State.json", semantics));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
    }

    private static byte[] CreateRecord(string fileName, string shortName, ulong fileIdLow, ulong fileIdHigh)
    {
        var fileNameBytes = Encoding.Unicode.GetBytes(fileName);
        var shortNameBytes = Encoding.Unicode.GetBytes(shortName);
        var buffer = new byte[114 + fileNameBytes.Length];
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(40), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(56), WindowsNative.FileAttributeNormal);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(60), checked((uint)fileNameBytes.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(72), fileIdLow);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(80), fileIdHigh);
        buffer[88] = checked((byte)shortNameBytes.Length);
        shortNameBytes.CopyTo(buffer, 90);
        fileNameBytes.CopyTo(buffer, 114);
        return buffer;
    }

    private static byte[] Mutate(byte[] source, int offset, byte value)
    {
        var result = source.ToArray();
        result[offset] = value;
        return result;
    }

    private static byte[] MutateUInt32(byte[] source, int offset, uint value)
    {
        var result = source.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset), value);
        return result;
    }

    private static byte[] MutateUInt64(byte[] source, int offset, ulong value)
    {
        var result = source.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(offset), value);
        return result;
    }
}
