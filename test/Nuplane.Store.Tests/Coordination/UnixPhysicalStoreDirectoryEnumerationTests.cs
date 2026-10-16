using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Unix")]
public sealed class UnixPhysicalStoreDirectoryEnumerationTests
{
    [SupportedUnixFact]
    public void EnumerateChildNamesNoFollow_EmptyDirectory_ReturnsEmptyList()
    {
        using var context = UnixPhysicalStorePublicationTests.CreateContext();
        var files = context.FileSystem;
        var parent = context.Parent;

        var names = Enumerate(files, parent, maximumEntries: 1);

        Assert.Empty(names);
    }

    [SupportedUnixFact]
    public void EnumerateChildNamesNoFollow_ReturnsExactFileDirectoryAndLinkNamesInOrdinalOrder()
    {
        using var context = UnixPhysicalStorePublicationTests.CreateContext();
        var files = context.FileSystem;
        var installPath = context.ParentPath;
        var unicodeName = "café-雪.txt";
        Directory.CreateDirectory(Path.Combine(installPath, "real-directory"));
        File.WriteAllText(Path.Combine(installPath, "ordinary-file.txt"), "payload");
        File.WriteAllText(Path.Combine(installPath, unicodeName), "unicode payload");
        File.CreateSymbolicLink(Path.Combine(installPath, "file-link"), "ordinary-file.txt");
        Directory.CreateSymbolicLink(Path.Combine(installPath, "directory-link"), "real-directory");
        var parent = context.Parent;

        var names = Enumerate(files, parent, maximumEntries: 5);

        Assert.Equal(
            new[] { "directory-link", "file-link", "ordinary-file.txt", "real-directory", unicodeName }
                .OrderBy(name => name, StringComparer.Ordinal),
            names);
    }

    [SupportedUnixFact]
    public async Task EnumerateChildNamesNoFollow_UsesAnIndependentCursorForEachCall()
    {
        using var context = UnixPhysicalStorePublicationTests.CreateContext();
        var files = context.FileSystem;
        var parent = context.Parent;
        File.WriteAllText(Path.Combine(context.ParentPath, "first"), "first");
        File.WriteAllText(Path.Combine(context.ParentPath, "second"), "second");

        var first = Enumerate(files, parent, maximumEntries: 2);
        var second = Enumerate(files, parent, maximumEntries: 2);

        Assert.Equal(new[] { "first", "second" }, first);
        Assert.Equal(first, second);
        var concurrent = await Task.WhenAll(
            Task.Run(() => Enumerate(files, parent, maximumEntries: 2)),
            Task.Run(() => Enumerate(files, parent, maximumEntries: 2)));
        Assert.All(concurrent, result => Assert.Equal(first, result));
    }

    [SupportedUnixFact]
    public void EnumerateChildNamesNoFollow_EnforcesOneGlobalBoundAcrossNativeBatches()
    {
        const int entryCount = 160;
        using var context = UnixPhysicalStorePublicationTests.CreateContext();
        var files = context.FileSystem;
        var parent = context.Parent;
        var expected = new List<string>(entryCount);
        for (var index = 0; index < entryCount; index++)
        {
            var name = $"entry-{index:D3}-{new string('x', 220)}";
            expected.Add(name);
            File.WriteAllBytes(Path.Combine(context.ParentPath, name), []);
        }

        var names = Enumerate(files, parent, maximumEntries: entryCount);
        var refusal = Assert.Throws<PackageStoreAdmissionException>(
            () => Enumerate(files, parent, maximumEntries: entryCount - 1));

        Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal), names);
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
    }

    [SupportedUnixFact]
    public void EnumerateChildNamesNoFollow_UsesHeldDirectoryAfterItsTextualPathIsReplaced()
    {
        using var context = UnixPhysicalStorePublicationTests.CreateContext();
        var files = context.FileSystem;
        var originalPath = context.ParentPath;
        File.WriteAllText(Path.Combine(originalPath, "original-child"), "original");
        var held = context.Parent;

        Directory.Move(originalPath, context.Fixture.GetPath("moved-directory"));
        Directory.CreateDirectory(originalPath);
        File.WriteAllText(Path.Combine(originalPath, "replacement-child"), "replacement");

        Assert.Equal(new[] { "original-child" }, Enumerate(files, held, maximumEntries: 1));
        using var replacement = PhysicalStoreTestDirectory.Open(files, originalPath);
        Assert.Equal(new[] { "replacement-child" }, Enumerate(files, replacement, maximumEntries: 1));
    }

    [SupportedUnixFact]
    public void EnumerateChildNamesNoFollow_RefusesInvalidBoundForeignAndClosedHandles()
    {
        using var context = UnixPhysicalStorePublicationTests.CreateContext();
        var files = context.FileSystem;
        var parent = context.Parent;

        Assert.Throws<ArgumentOutOfRangeException>(() => Enumerate(files, parent, maximumEntries: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Enumerate(files, parent, maximumEntries: -1));

        var foreign = new UnixPhysicalStoreFileSystem();
        var foreignRefusal = Assert.Throws<PackageStoreAdmissionException>(
            () => Enumerate(foreign, parent, maximumEntries: 1));
        Assert.Equal(PackageStoreAdmissionReason.RootMismatch, foreignRefusal.Reason);

        parent.Dispose();
        var closedRefusal = Assert.Throws<PackageStoreAdmissionException>(
            () => Enumerate(files, parent, maximumEntries: 1));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, closedRefusal.Reason);
    }

    [SupportedUnixFact]
    public void EnumerateChildNamesNoFollow_RefusesNonPortableComponentsWithoutReturningNames()
    {
        using var context = UnixPhysicalStorePublicationTests.CreateContext();
        var files = context.FileSystem;
        var parent = context.Parent;
        File.WriteAllText(Path.Combine(context.ParentPath, "valid-first"), "valid");
        File.WriteAllText(Path.Combine(context.ParentPath, "invalid:name"), "invalid portable component");

        var refusal = Assert.Throws<PackageStoreAdmissionException>(
            () => Enumerate(files, parent, maximumEntries: 2));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
    }

    [LinuxExt4CasefoldFact]
    public void EnumerateChildNamesNoFollow_Ext4CasefoldPreservesExactStoredUnicodeSpelling()
    {
        using var context = UnixPhysicalStorePublicationTests.CreateContext(enableCasefold: true);
        var installPath = context.ParentPath;
        const string storedName = "Package-café-雪";
        File.WriteAllText(Path.Combine(installPath, storedName), "payload");
        var files = context.FileSystem;
        var parent = context.Parent;

        var names = Enumerate(files, parent, maximumEntries: 1);

        Assert.Equal(new[] { storedName }, names);
    }

    private static IReadOnlyList<string> Enumerate(
        IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle parent,
        int maximumEntries)
        => ((IPhysicalStoreDirectoryEnumerationFileSystem)files).EnumerateChildNamesNoFollow(parent, maximumEntries);
}
