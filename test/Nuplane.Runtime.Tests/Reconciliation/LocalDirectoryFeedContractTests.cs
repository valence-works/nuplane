using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Nuplane.Abstractions;
using Nuplane.Feeds;
using Nuplane.Feeds.Configuration;
using Nuplane.Feeds.Policy;
using Nuplane.Feeds.Setup;
using Nuplane.Feeds.Versioning;
using Nuplane.Sources.Directory.Builder;
using Nuplane.Runtime.Tests.TestSupport;

namespace Nuplane.Runtime.Tests.Reconciliation;

/// <summary>
/// Contract tests verifying that local directory feeds (file:// scheme) are
/// eligible candidates for resolution without any remote feeds configured.
/// </summary>
public sealed class LocalDirectoryFeedContractTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nuplane-local-feed-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private FeedResolutionOptions CreateLocalFeedOptions()
    {
        var options = new FeedResolutionOptions();
        options.Feeds.Add(new("local-drop", new("file:///" + _tempDir.Replace('\\', '/').TrimStart('/'))));
        return options;
    }

    private static MultiFeedPackageResolver CreateResolver(
        FeedResolutionOptions options,
        IFeedVersionEnumerator? versionEnumerator = null)
    {
        var wrappedOptions = new OptionsWrapper<FeedResolutionOptions>(options);
        return new(
            wrappedOptions,
            new FeedResolutionPolicy(wrappedOptions),
            Substitute.For<IRemotePackageAcquirer>(),
            versionEnumerator ?? Substitute.For<IFeedVersionEnumerator>(),
            Substitute.For<IVersionRangeEvaluator>(),
            NullLogger<MultiFeedPackageResolver>.Instance);
    }

    [Fact]
    public async Task Resolve_WithOnlyLocalFeed_Succeeds()
    {
        NupkgTestBuilder.Create("MyPlugin", "1.0.0").BuildTo(_tempDir);
        var resolver = CreateResolver(CreateLocalFeedOptions());

        var request = new PackageRequest("MyPlugin", "1.0.0", "local-drop", PackageUpdatePolicy.Exact, "local-source");
        var result = await resolver.ResolveAsync(request, CancellationToken.None);

        Assert.Equal("MyPlugin", result.Id);
        Assert.Equal("local-drop", result.FeedName);
        Assert.True(Directory.Exists(result.InstallPath), $"Install path '{result.InstallPath}' should exist on disk.");
    }

    [Fact]
    public async Task Resolve_WithLocalFeedOnly_NoExplicitFeedNameOnRequest_StillResolvesViaPriority()
    {
        NupkgTestBuilder.Create("MyPlugin", "1.0.0").BuildTo(_tempDir);
        var resolver = CreateResolver(CreateLocalFeedOptions());

        // No explicit feed name; resolution should still find the local feed via priority ordering
        var request = new PackageRequest("MyPlugin", "1.0.0", FeedName: null, PackageUpdatePolicy.Exact, "local-source");
        var result = await resolver.ResolveAsync(request, CancellationToken.None);

        Assert.Equal("MyPlugin", result.Id);
        Assert.Equal("local-drop", result.FeedName);
        Assert.True(Directory.Exists(result.InstallPath), $"Install path '{result.InstallPath}' should exist on disk.");
    }

    [Fact]
    public async Task CacheOnlyFeed_ResolvesExactPackage()
    {
        NupkgTestBuilder.Create("CachedPlugin", "1.2.3").BuildTo(_tempDir);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(nuplane =>
        {
            nuplane.AddDirectoryFeed("local-cache", _tempDir, feed =>
            {
                feed.Role = DirectoryFeedRole.Cache;
                feed.Watch = false;
            });
        });

        using var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<IPackageResolver>();
        var request = new PackageRequest("CachedPlugin", "1.2.3", "local-cache", PackageUpdatePolicy.Exact, "test-source");

        var result = await resolver.ResolveAsync(request, CancellationToken.None);

        Assert.Equal("CachedPlugin", result.Id);
        Assert.Equal("local-cache", result.FeedName);
        Assert.True(Directory.Exists(result.InstallPath), $"Install path '{result.InstallPath}' should exist on disk.");
    }


    [Fact]
    public async Task CacheOnlyFeed_OfflineMode_ResolvesRootRequestedFromRemoteFeed()
    {
        NupkgTestBuilder.Create("BakedPlugin", "1.2.3").BuildTo(_tempDir);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(nuplane =>
        {
            nuplane.AddFeed("nuget.org", feed => feed
                .FromUri(new("https://api.nuget.org/v3/index.json"))
                .Include("BakedPlugin [1.2.3]"));
            nuplane.AddDirectoryFeed("baked-packages", _tempDir, feed =>
            {
                feed.Role = DirectoryFeedRole.Cache;
                feed.Watch = false;
            });
        });
        services.Configure<FeedResolutionOptions>(options => options.OfflineMode = true);

        using var provider = services.BuildServiceProvider();

        var desired = new List<PackageRequest>();
        foreach (var source in provider.GetServices<IDesiredPackageSource>())
        {
            desired.AddRange(await source.GetDesiredAsync(CancellationToken.None));
        }

        // The cache-role directory contributes no desired roots, so the only root is the one the
        // operator asked for, pinned to the remote feed that declared it.
        var root = Assert.Single(desired);
        Assert.Equal("BakedPlugin", root.Id);
        Assert.Equal("nuget.org", root.FeedName);

        var resolver = provider.GetRequiredService<IPackageResolver>();
        var result = await resolver.ResolveAsync(root, CancellationToken.None);

        Assert.Equal("baked-packages", result.FeedName);
        Assert.Equal("1.2.3", result.Version);
        Assert.True(Directory.Exists(result.InstallPath), $"Install path '{result.InstallPath}' should exist on disk.");
    }

    [Fact]
    public async Task Resolve_ExactVersion_WhenNupkgFileNameCasingDiffers_ResolvesFromLocalFeed()
    {
        // Restore writes lower-cased file names; nuspec dependencies declare canonical ids.
        NupkgTestBuilder.Create("sourcegear.sqlite3", "1.0.0").BuildTo(_tempDir);
        var resolver = CreateResolver(CreateLocalFeedOptions());

        var request = new PackageRequest("SourceGear.sqlite3", "1.0.0", FeedName: null, PackageUpdatePolicy.Exact, "local-source");
        var result = await resolver.ResolveAsync(request, CancellationToken.None);

        Assert.Equal("local-drop", result.FeedName);
        Assert.Equal("1.0.0", result.Version);
        Assert.True(Directory.Exists(result.InstallPath), $"Install path '{result.InstallPath}' should exist on disk.");
        Assert.Equal(
            Path.Combine(_tempDir, ".installed", "sourcegear.sqlite3", "1.0.0"),
            result.InstallPath);
    }

    [Fact]
    public async Task Resolve_DependencyRange_SelectsVersionPresentInDirectory()
    {
        // The directory holds 1.2.0 while the dependency asks for "1.0.0 or newer"; the lower bound
        // of the range is not on disk, so the version must come from the directory contents.
        NupkgTestBuilder.Create("MyPlugin", "1.2.0").BuildTo(_tempDir);
        var resolver = CreateResolver(CreateLocalFeedOptions());

        var request = new PackageRequest("MyPlugin", "[1.0.0, )", FeedName: null, PackageUpdatePolicy.Exact, "dependency-of:MyRoot");
        var result = await resolver.ResolveAsync(request, CancellationToken.None);

        Assert.Equal("local-drop", result.FeedName);
        Assert.Equal("1.2.0", result.Version);
        Assert.True(Directory.Exists(result.InstallPath), $"Install path '{result.InstallPath}' should exist on disk.");
    }

    [Fact]
    public async Task Resolve_DependencyRange_WithUnreachableRemoteFeed_ResolvesFromLocalFeed()
    {
        NupkgTestBuilder.Create("sourcegear.sqlite3", "1.0.0").BuildTo(_tempDir);

        var options = CreateLocalFeedOptions();
        options.Feeds.Add(new("nuget.org", new("https://api.nuget.org/v3/index.json")));
        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        enumerator.EnumerateVersionsAsync(Arg.Any<FeedDefinition>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<PackageVersionList>>(_ => throw new InvalidOperationException(
                "Unable to load the service index for source https://api.nuget.org/v3/index.json."));
        var resolver = CreateResolver(options, enumerator);

        var request = new PackageRequest("SourceGear.sqlite3", "[1.0.0, )", FeedName: null, PackageUpdatePolicy.Exact, "dependency-of:MyRoot");
        var result = await resolver.ResolveAsync(request, CancellationToken.None);

        Assert.Equal("local-drop", result.FeedName);
        Assert.Equal("1.0.0", result.Version);
        await enumerator.DidNotReceiveWithAnyArgs().EnumerateVersionsAsync(default!, default!, default);
    }

    [Fact]
    public async Task Resolve_WhenLocalFeedMissesAndRemoteFeedFails_FailureNamesBothFeeds()
    {
        NupkgTestBuilder.Create("OtherPlugin", "1.0.0").BuildTo(_tempDir);

        var options = CreateLocalFeedOptions();
        options.Feeds.Add(new("nuget.org", new("https://api.nuget.org/v3/index.json")));
        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        enumerator.EnumerateVersionsAsync(Arg.Any<FeedDefinition>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<PackageVersionList>>(_ => throw new InvalidOperationException("Unable to load the service index."));
        var resolver = CreateResolver(options, enumerator);

        var request = new PackageRequest("MyPlugin", "[1.0.0, )", FeedName: null, PackageUpdatePolicy.Exact, "dependency-of:MyRoot");

        var exception = await Assert.ThrowsAsync<NoEligibleFeedException>(
            () => resolver.ResolveAsync(request, CancellationToken.None));

        Assert.Contains("local-drop", exception.Message, StringComparison.Ordinal);
        Assert.Contains("nuget.org", exception.Message, StringComparison.Ordinal);
        Assert.True(resolver.TryGetDecision("MyPlugin", out var decision));
        Assert.Contains("local-directory-version-no-match", decision.DecisionPath, StringComparison.Ordinal);
        Assert.Contains("version-enumeration-error", decision.DecisionPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_WithNoFeeds_ThrowsForNoEligibleFeed()
    {
        var resolver = CreateResolver(new FeedResolutionOptions());

        var request = new PackageRequest("MyPlugin", "1.0.0", FeedName: null, PackageUpdatePolicy.Exact, "local-source");

        await Assert.ThrowsAsync<NoEligibleFeedException>(
            () => resolver.ResolveAsync(request, CancellationToken.None));
    }

    [Fact]
    public void OrderCandidates_LocalFileUriFeed_IncludedInCandidates()
    {
        var localFeed = new FeedDefinition("local-drop", new("file:///packages/local"));
        var opts = new FeedResolutionOptions();
        opts.Feeds.Add(localFeed);
        var policy = new FeedResolutionPolicy(new OptionsWrapper<FeedResolutionOptions>(opts));

        var request = new PackageRequest("MyPlugin", "1.0.0", FeedName: null, PackageUpdatePolicy.Exact, "local-source");
        var candidates = policy.OrderCandidates(request);

        Assert.Single(candidates);
        Assert.Equal("local-drop", candidates[0].Name);
    }
}
