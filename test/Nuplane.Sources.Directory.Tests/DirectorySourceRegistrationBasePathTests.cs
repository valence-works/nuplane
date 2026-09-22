using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nuplane.Feeds.Configuration;
using Nuplane.Sources.Directory.Builder;
using Nuplane.Sources.Directory.Registration;

namespace Nuplane.Sources.Directory.Tests;

/// <summary>
/// Covers how <see cref="DirectorySourceRegistrationServices.RegisterFeed"/> resolves a relative
/// <c>DirectoryPath</c> against the optional <c>basePath</c> parameter, instead of always anchoring
/// it to the process's current directory.
/// <see cref="Nuplane.Integration.Tests.Restore.NuplaneRestoreTests"/> covers that base path
/// actually reaching registration end to end through a host-free restore.
/// </summary>
public sealed class DirectorySourceRegistrationBasePathTests
{
    private const string FeedName = "relative-feed";
    private const string RelativeDirectoryPath = "packages";

    [Fact]
    public void RegisterFeed_WithNoBasePath_ResolvesARelativeDirectoryPathAgainstTheCurrentDirectory()
    {
        var services = new ServiceCollection();

        DirectorySourceRegistrationServices.RegisterFeed(services, FeedName, CreateOptions(), [], null);

        Assert.Equal(Path.GetFullPath(RelativeDirectoryPath), ResolvedPath(services));
    }

    [Fact]
    public void RegisterFeed_WithAnAbsoluteBasePath_ResolvesARelativeDirectoryPathAgainstItInsteadOfTheCurrentDirectory()
    {
        var basePath = CreateTempDirectoryPath("base");
        var services = new ServiceCollection();

        DirectorySourceRegistrationServices.RegisterFeed(services, FeedName, CreateOptions(), [], null, basePath);

        var resolved = ResolvedPath(services);
        Assert.Equal(Path.GetFullPath(Path.Combine(basePath, RelativeDirectoryPath)), resolved);
        Assert.NotEqual(Path.GetFullPath(RelativeDirectoryPath), resolved);
    }

    [Fact]
    public void RegisterFeed_WithAnAlreadyAbsoluteDirectoryPath_IgnoresTheBasePath()
    {
        var absoluteDirectoryPath = CreateTempDirectoryPath("absolute-feed-dir");
        var basePath = CreateTempDirectoryPath("unrelated-base");
        var services = new ServiceCollection();

        DirectorySourceRegistrationServices.RegisterFeed(
            services, FeedName, CreateOptions(absoluteDirectoryPath), [], null, basePath);

        Assert.Equal(Path.GetFullPath(absoluteDirectoryPath), ResolvedPath(services));
    }

    [Fact]
    public void RegisterFeed_WithANonAbsoluteBasePath_ThrowsArgumentException()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentException>(() =>
            DirectorySourceRegistrationServices.RegisterFeed(
                services, FeedName, CreateOptions(), [], null, "relative-base"));

        Assert.Contains("absolute", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateTempDirectoryPath(string label) =>
        Path.Combine(Path.GetTempPath(), $"nuplane-dir-basepath-{label}", Guid.NewGuid().ToString("N"));

    private static NuplaneDirectoryFeedOptions CreateOptions(string directoryPath = RelativeDirectoryPath) =>
        new()
        {
            DirectoryPath = directoryPath,
            Watch = false,
            DebounceWindow = TimeSpan.FromSeconds(1),
        };

    /// <summary>
    /// Reads back the resolved, absolute directory path <see cref="DirectorySourceRegistrationServices.RegisterFeed"/>
    /// registered, through the feed's resolution <c>Uri</c> — the same feed-role default
    /// (<see cref="DirectoryFeedRole.DesiredAndCache"/>) <see cref="CreateOptions"/> uses ensures one
    /// is always registered.
    /// </summary>
    private static string ResolvedPath(ServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        var feed = Assert.Single(provider.GetRequiredService<IOptions<FeedResolutionOptions>>().Value.Feeds);
        return feed.ServiceIndex.LocalPath;
    }
}
