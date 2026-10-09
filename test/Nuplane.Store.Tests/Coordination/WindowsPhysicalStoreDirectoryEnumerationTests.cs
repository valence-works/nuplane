using System.Buffers.Binary;
using System.Text;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Windows;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Windows")]
public sealed class WindowsPhysicalStoreDirectoryEnumerationTests
{
    [SupportedWindowsFact]
    public async Task Enumerate_ReturnsExactSortedDirectNamesIncludingLinksAndUsesIndependentCursors()
    {
        using var context = WindowsPhysicalStorePublicationTests.CreateContext();
        var names = Assert.IsAssignableFrom<IPhysicalStoreDirectoryEnumerationFileSystem>(context.FileSystem);
        Assert.Empty(names.EnumerateChildNamesNoFollow(context.Parent, maximumEntries: 10));
        File.WriteAllText(Path.Combine(context.ParentPath, "plain.txt"), "contents");
        File.WriteAllText(Path.Combine(context.ParentPath, ".hidden"), "contents");
        File.WriteAllText(Path.Combine(context.ParentPath, "Cafe\u0301.txt"), "contents");
        Directory.CreateDirectory(Path.Combine(context.ParentPath, "nested"));
        File.CreateSymbolicLink(
            Path.Combine(context.ParentPath, "file-link"),
            Path.Combine(context.ParentPath, "plain.txt"));

        var expected = new[] { ".hidden", "Cafe\u0301.txt", "file-link", "nested", "plain.txt" }
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        var first = names.EnumerateChildNamesNoFollow(context.Parent, maximumEntries: expected.Length);
        var second = names.EnumerateChildNamesNoFollow(context.Parent, maximumEntries: expected.Length);
        var concurrent = await Task.WhenAll(
            Task.Run(() => names.EnumerateChildNamesNoFollow(context.Parent, expected.Length)),
            Task.Run(() => names.EnumerateChildNamesNoFollow(context.Parent, expected.Length)));

        Assert.Equal(expected, first);
        Assert.Equal(expected, second);
        Assert.Equal(expected, concurrent[0]);
        Assert.Equal(expected, concurrent[1]);
        Assert.Equal(PhysicalStoreEntryKind.SymbolicLink, context.FileSystem.InspectChildNoFollow(context.Parent, "file-link")!.Kind);
    }

    [SupportedWindowsFact]
    public void Enumerate_MultipleNativeBatchesPreserveEveryNameAndEnforceGlobalBound()
    {
        using var context = WindowsPhysicalStorePublicationTests.CreateContext();
        var names = Assert.IsAssignableFrom<IPhysicalStoreDirectoryEnumerationFileSystem>(context.FileSystem);
        var prefix = new string('n', 160);
        var expected = Enumerable.Range(0, 512).Select(index => $"{prefix}-{index:D4}").ToArray();
        foreach (var name in expected)
            File.WriteAllText(Path.Combine(context.ParentPath, name), string.Empty);

        Assert.Equal(expected, names.EnumerateChildNamesNoFollow(context.Parent, expected.Length));
        Assert.Throws<PackageStoreAdmissionException>(() =>
            names.EnumerateChildNamesNoFollow(context.Parent, expected.Length - 1));
    }

    [SupportedWindowsFact]
    public void Enumerate_HeldDirectoryPreventsLocatorReplacementAndKeepsOwnedEntriesVisible()
    {
        using var context = WindowsPhysicalStorePublicationTests.CreateContext();
        var names = Assert.IsAssignableFrom<IPhysicalStoreDirectoryEnumerationFileSystem>(context.FileSystem);
        File.WriteAllText(Path.Combine(context.ParentPath, "held-entry"), string.Empty);
        // The normal provider handle denies delete sharing, so Windows prevents replacement
        // of this locator while the directory is held. Unix qualifies the renamed-handle case.
        Assert.ThrowsAny<IOException>(() => Directory.Move(context.ParentPath, context.ParentPath + "-moved"));

        Assert.Equal(new[] { "held-entry" }, names.EnumerateChildNamesNoFollow(context.Parent, 10));
    }

    [SupportedWindowsFact]
    public void Enumerate_RequiresPositiveBoundAndRefusesOverflowWithoutReturningPartialNames()
    {
        using var context = WindowsPhysicalStorePublicationTests.CreateContext();
        var names = Assert.IsAssignableFrom<IPhysicalStoreDirectoryEnumerationFileSystem>(context.FileSystem);
        File.WriteAllText(Path.Combine(context.ParentPath, "one"), "1");
        File.WriteAllText(Path.Combine(context.ParentPath, "two"), "2");

        Assert.Throws<ArgumentOutOfRangeException>(() => names.EnumerateChildNamesNoFollow(context.Parent, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => names.EnumerateChildNamesNoFollow(context.Parent, -1));
        Assert.Equal(2, names.EnumerateChildNamesNoFollow(context.Parent, 2).Count);

        var refusal = Assert.Throws<PackageStoreAdmissionException>(
            () => names.EnumerateChildNamesNoFollow(context.Parent, 1));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
    }

    [SupportedWindowsFact]
    public void Enumerate_RefusesForeignAndClosedDirectoryHandles()
    {
        using var context = WindowsPhysicalStorePublicationTests.CreateContext();
        var names = Assert.IsAssignableFrom<IPhysicalStoreDirectoryEnumerationFileSystem>(context.FileSystem);
        IPhysicalStoreDirectoryEnumerationFileSystem foreign = new WindowsPhysicalStoreFileSystem();

        var foreignRefusal = Assert.Throws<PackageStoreAdmissionException>(
            () => foreign.EnumerateChildNamesNoFollow(context.Parent, 10));
        Assert.Equal(PackageStoreAdmissionReason.RootMismatch, foreignRefusal.Reason);

        context.Parent.Dispose();
        var closedRefusal = Assert.Throws<PackageStoreAdmissionException>(
            () => names.EnumerateChildNamesNoFollow(context.Parent, 10));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, closedRefusal.Reason);
    }

    [Fact]
    public void DirectoryQueryStatus_RecognizesOnlyConsistentZeroByteTerminalResults()
    {
        const int noMoreFiles = unchecked((int)0x80000006);
        const int noSuchFile = unchecked((int)0xC000000F);
        Assert.True(WindowsNative.IsDirectoryEnumerationEnd(noSuchFile, noSuchFile, 0, isFirstQuery: true));
        Assert.True(WindowsNative.IsDirectoryEnumerationEnd(noSuchFile, 0, 0, isFirstQuery: true));
        Assert.True(WindowsNative.IsDirectoryEnumerationEnd(noMoreFiles, noMoreFiles, 0, isFirstQuery: false));
        Assert.False(WindowsNative.IsDirectoryEnumerationEnd(noSuchFile, noSuchFile, 0, isFirstQuery: false));
        Assert.False(WindowsNative.IsDirectoryEnumerationEnd(0, 0, 0, isFirstQuery: true));
        Assert.Throws<WindowsNativeCallException>(() =>
            WindowsNative.IsDirectoryEnumerationEnd(noSuchFile, noSuchFile, 12, isFirstQuery: true));
        Assert.Throws<WindowsNativeCallException>(() =>
            WindowsNative.IsDirectoryEnumerationEnd(noMoreFiles, noSuchFile, 0, isFirstQuery: true));
    }

    [Fact]
    public void ParseDirectoryRecords_RejectsMalformedAndDuplicateNames()
    {
        Assert.Throws<WindowsNativeCallException>(() => WindowsNative.ParseDirectoryNamesInformation(
            CreateRecords("same", "same"), maximumEntries: 10));
        Assert.Throws<WindowsNativeCallException>(() => WindowsNative.ParseDirectoryNamesInformation(
            CreateRecord("name")[..^1], maximumEntries: 10));
        Assert.Throws<WindowsNativeCallException>(() => WindowsNative.ParseDirectoryNamesInformation(
            CreateRecord("x", nextEntryOffset: 12), maximumEntries: 10));
        Assert.Throws<WindowsNativeCallException>(() => WindowsNative.ParseDirectoryNamesInformation(
            CreateRecord("invalid/name"), maximumEntries: 10));
        Assert.Throws<WindowsNativeCallException>(() => WindowsNative.ParseDirectoryNamesInformation(
            CreateRecordFromRawName([0x00, 0xD8]), maximumEntries: 10));

        Assert.Equal(
            new[] { "name" },
            WindowsNative.ParseDirectoryNamesInformation(CreateRecords(".", "..", "name"), maximumEntries: 10));
    }

    private static byte[] CreateRecords(params string[] names)
    {
        var encoded = names.Select(static name => Encoding.Unicode.GetBytes(name)).ToArray();
        var sizes = encoded.Select(static bytes => Align4(12 + bytes.Length)).ToArray();
        var totalLength = sizes.Take(Math.Max(0, sizes.Length - 1)).Sum() +
                          (sizes.Length == 0 ? 0 : 12 + encoded[^1].Length);
        var buffer = new byte[totalLength];
        var offset = 0;
        for (var index = 0; index < encoded.Length; index++)
        {
            var name = encoded[index];
            var next = index == encoded.Length - 1 ? 0 : checked((uint)sizes[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), next);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 8), checked((uint)name.Length));
            name.CopyTo(buffer, offset + 12);
            offset += sizes[index];
        }

        return buffer;
    }

    private static byte[] CreateRecord(string name, uint nextEntryOffset = 0)
    {
        var encoded = Encoding.Unicode.GetBytes(name);
        var buffer = new byte[12 + encoded.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, nextEntryOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), checked((uint)encoded.Length));
        encoded.CopyTo(buffer, 12);
        return buffer;
    }

    private static byte[] CreateRecordFromRawName(byte[] nameBytes)
    {
        var buffer = new byte[12 + nameBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), checked((uint)nameBytes.Length));
        nameBytes.CopyTo(buffer, 12);
        return buffer;
    }

    private static int Align4(int value) => checked((value + 3) & ~3);
}
