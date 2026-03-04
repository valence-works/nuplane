using Nuplane.Abstractions;
using Nuplane.Sources.Directory;

namespace Nuplane.Runtime.Tests.Sources.Directory;

/// <summary>
/// Unit tests verifying that <see cref="DirectoryNupkgDesiredSource"/> sets
/// FeedName and SourceName attribution correctly on produced package requests.
/// </summary>
public sealed class DirectoryNupkgDesiredSourceTests : IDisposable
{
    private readonly string tempDir;

    public DirectoryNupkgDesiredSourceTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"nuplane-test-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(tempDir))
        {
            System.IO.Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetDesiredAsync_SetsFeedName_WhenProvided()
    {
        CreateNupkg("MyPlugin.1.0.0.nupkg");
        var source = new DirectoryNupkgDesiredSource("src-name", tempDir, feedName: "local-drop");

        var results = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("local-drop", results[0].FeedName);
    }

    [Fact]
    public async Task GetDesiredAsync_SetsSourceName_ToProvidedValue()
    {
        CreateNupkg("MyPlugin.1.0.0.nupkg");
        var source = new DirectoryNupkgDesiredSource("my-custom-source", tempDir, feedName: "local-drop");

        var results = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("my-custom-source", results[0].SourceName);
    }

    [Fact]
    public async Task GetDesiredAsync_FeedNameNull_SetsFeedNameToNull()
    {
        CreateNupkg("MyPlugin.1.0.0.nupkg");
        var source = new DirectoryNupkgDesiredSource("src-name", tempDir, feedName: null);

        var results = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Single(results);
        Assert.Null(results[0].FeedName);
    }

    [Fact]
    public async Task GetDesiredAsync_ParsesPackageIdAndVersion()
    {
        CreateNupkg("Acme.Widgets.2.3.1.nupkg");
        var source = new DirectoryNupkgDesiredSource("src-name", tempDir, feedName: "local-drop");

        var results = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("Acme.Widgets", results[0].Id);
        Assert.Equal("2.3.1", results[0].VersionRange);
    }

    [Fact]
    public async Task GetDesiredAsync_SetsExactUpdatePolicy()
    {
        CreateNupkg("MyPlugin.1.0.0.nupkg");
        var source = new DirectoryNupkgDesiredSource("src-name", tempDir, feedName: "local-drop");

        var results = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(PackageUpdatePolicy.Exact, results[0].UpdatePolicy);
    }

    [Fact]
    public async Task GetDesiredAsync_MultiplePackages_AllHaveSameFeedAndSource()
    {
        CreateNupkg("PluginA.1.0.0.nupkg");
        CreateNupkg("PluginB.2.0.0.nupkg");
        var source = new DirectoryNupkgDesiredSource("dir-src", tempDir, feedName: "feed-x");

        var results = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.Equal("feed-x", r.FeedName);
            Assert.Equal("dir-src", r.SourceName);
        });
    }

    [Fact]
    public async Task GetDesiredAsync_EmptyDirectory_ReturnsEmpty()
    {
        var source = new DirectoryNupkgDesiredSource("src-name", tempDir, feedName: "local-drop");

        var results = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetDesiredAsync_NonExistentDirectory_ReturnsEmpty()
    {
        var nonExistent = Path.Combine(tempDir, "does-not-exist");
        var source = new DirectoryNupkgDesiredSource("src-name", nonExistent, feedName: "local-drop");

        var results = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Empty(results);
    }

    private void CreateNupkg(string fileName)
    {
        File.WriteAllBytes(Path.Combine(tempDir, fileName), [0x50, 0x4B, 0x03, 0x04]);
    }
}
