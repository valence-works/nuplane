using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests;

public sealed class PackageStoreFixtureTests
{
    [Fact]
    public void Constructor_CreatesUniquePackageRootsAndStateSlots()
    {
        using var first = new PackageStoreFixture();
        using var second = new PackageStoreFixture();

        Assert.NotEqual(first.RootPath, second.RootPath);
        Assert.NotEqual(first.PackageInstallRoot, second.PackageInstallRoot);
        Assert.NotEqual(first.StateFilePath, second.StateFilePath);
        Assert.True(Directory.Exists(first.RootPath));
        Assert.True(Directory.Exists(first.PackageInstallRoot));
        Assert.True(Directory.Exists(Path.GetDirectoryName(first.StateFilePath)!));
        Assert.False(File.Exists(first.StateFilePath));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("packages/../../outside")]
    [InlineData("packages/../state.json")]
    [InlineData("C:\\outside\\state.json")]
    [InlineData("C:/outside/state.json")]
    [InlineData("\\\\server\\share\\state.json")]
    public void GetPath_WhenPathEscapesOrIsRooted_ThrowsArgumentException(string path)
    {
        using var fixture = new PackageStoreFixture();

        Assert.Throws<ArgumentException>(() => fixture.GetPath(path));
    }

    [Fact]
    public void GetPath_WhenPathIsRootedForCurrentPlatform_ThrowsArgumentException()
    {
        using var fixture = new PackageStoreFixture();
        var rootedPath = Path.Combine(Path.GetTempPath(), "outside-store-state.json");

        Assert.Throws<ArgumentException>(() => fixture.GetPath(rootedPath));
    }

    [Fact]
    public void CreateStateSlot_WhenNestedRelativePathIsValid_CreatesOnlyOwnedParent()
    {
        using var fixture = new PackageStoreFixture();

        var slotPath = fixture.CreateStateSlot("state/child/member.json");

        Assert.StartsWith(fixture.RootPath + Path.DirectorySeparatorChar, slotPath, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.GetDirectoryName(slotPath)!));
        Assert.False(File.Exists(slotPath));
    }

    [Fact]
    public void GetPath_WhenFinalOrIntermediateLinksExist_RefusesAndCleanupLeavesSiblingUntouched()
    {
        using var fixture = new PackageStoreFixture();
        using var sibling = new PackageStoreFixture();
        var siblingSentinel = Path.Combine(sibling.PackageInstallRoot, "keep.txt");
        File.WriteAllText(siblingSentinel, "owned by sibling fixture");
        var nestedFile = Path.Combine(fixture.CreateDirectory("nested/deeper"), "owned.txt");
        File.WriteAllText(nestedFile, "fixture-owned content");

        var directoryLink = fixture.GetPath("escape");
        Directory.CreateSymbolicLink(directoryLink, sibling.PackageInstallRoot);

        Assert.Throws<InvalidOperationException>(() => fixture.GetPath("escape"));
        Assert.Throws<InvalidOperationException>(() => fixture.GetPath("escape/keep.txt"));

        var fileLink = fixture.GetPath("state/external.json");
        File.CreateSymbolicLink(fileLink, siblingSentinel);
        Assert.Throws<InvalidOperationException>(() => fixture.GetPath("state/external.json"));

        fixture.Dispose();
        fixture.Dispose();

        Assert.False(Directory.Exists(fixture.RootPath));
        Assert.Throws<ObjectDisposedException>(() => fixture.GetPath("after-dispose"));
        Assert.True(File.Exists(siblingSentinel));
        Assert.Equal("owned by sibling fixture", File.ReadAllText(siblingSentinel));
    }
}
