using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Nuplane.Abstractions;
using Nuplane.Feeds;
using Nuplane.Feeds.Configuration;
using Nuplane.Feeds.Policy;
using Nuplane.Feeds.Versioning;
using Nuplane.Runtime.Tests.TestSupport;

namespace Nuplane.Runtime.Tests.Feeds;

public sealed class MultiFeedPackageResolverTests
{
    [Fact]
    public async Task ResolveAsync_RemoteFeed_SelectsHighestStableVersion()
    {
        var options = new FeedResolutionOptions();
        options.Feeds.Add(new("remote", new("https://feed.example/v3/index.json")));
        var wrappedOptions = new OptionsWrapper<FeedResolutionOptions>(options);
        var policy = new FeedResolutionPolicy(wrappedOptions);

        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        enumerator.EnumerateVersionsAsync(Arg.Any<FeedDefinition>(), "MyPlugin", Arg.Any<CancellationToken>())
            .Returns(new PackageVersionList("MyPlugin", "remote", ["1.0.0", "1.5.0", "2.0.0", "3.0.0-beta"], DateTimeOffset.UtcNow));

        var evaluator = Substitute.For<IVersionRangeEvaluator>();
        evaluator.SelectBestMatch("", Arg.Any<IReadOnlyList<string>>())
            .Returns(new VersionResolutionResult(true, "2.0.0", 4, null));

        var acquirer = Substitute.For<IRemotePackageAcquirer>();
        acquirer.AcquireAsync(Arg.Any<FeedDefinition>(), "MyPlugin", "2.0.0", Arg.Any<CancellationToken>())
            .Returns("/installed/MyPlugin/2.0.0");

        var resolver = new MultiFeedPackageResolver(
            wrappedOptions, policy, acquirer, enumerator, evaluator,
            NullLogger<MultiFeedPackageResolver>.Instance);

        var request = new PackageRequest("MyPlugin", "", "remote", PackageUpdatePolicy.Range, "source");
        var result = await resolver.ResolveAsync(request, CancellationToken.None);

        Assert.Equal("MyPlugin", result.Id);
        Assert.Equal("2.0.0", result.Version);
        Assert.Equal("remote", result.FeedName);
    }

    [Fact]
    public async Task ResolveAsync_RemoteFeed_PopulatesDecisionWithEnumerationMetadata()
    {
        var options = new FeedResolutionOptions();
        options.Feeds.Add(new("remote", new("https://feed.example/v3/index.json")));
        var wrappedOptions = new OptionsWrapper<FeedResolutionOptions>(options);
        var policy = new FeedResolutionPolicy(wrappedOptions);

        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        enumerator.EnumerateVersionsAsync(Arg.Any<FeedDefinition>(), "MyPlugin", Arg.Any<CancellationToken>())
            .Returns(new PackageVersionList("MyPlugin", "remote", ["1.0.0", "2.0.0", "3.0.0"], DateTimeOffset.UtcNow));

        var evaluator = Substitute.For<IVersionRangeEvaluator>();
        evaluator.SelectBestMatch("", Arg.Any<IReadOnlyList<string>>())
            .Returns(new VersionResolutionResult(true, "3.0.0", 3, null));

        var acquirer = Substitute.For<IRemotePackageAcquirer>();
        acquirer.AcquireAsync(Arg.Any<FeedDefinition>(), "MyPlugin", "3.0.0", Arg.Any<CancellationToken>())
            .Returns("/installed/MyPlugin/3.0.0");

        var resolver = new MultiFeedPackageResolver(
            wrappedOptions, policy, acquirer, enumerator, evaluator,
            NullLogger<MultiFeedPackageResolver>.Instance);

        var request = new PackageRequest("MyPlugin", "", "remote", PackageUpdatePolicy.Range, "source");
        await resolver.ResolveAsync(request, CancellationToken.None);

        Assert.True(resolver.TryGetDecision("MyPlugin", out var decision));
        Assert.Equal(3, decision.EnumeratedVersionCount);
        Assert.False(decision.CacheHit);
    }

    [Fact]
    public async Task ResolveAsync_RemoteFeed_NoMatchingVersion_ThrowsNoEligibleFeedException()
    {
        var options = new FeedResolutionOptions();
        options.Feeds.Add(new("remote", new("https://feed.example/v3/index.json")));
        var wrappedOptions = new OptionsWrapper<FeedResolutionOptions>(options);
        var policy = new FeedResolutionPolicy(wrappedOptions);

        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        enumerator.EnumerateVersionsAsync(Arg.Any<FeedDefinition>(), "MyPlugin", Arg.Any<CancellationToken>())
            .Returns(new PackageVersionList("MyPlugin", "remote", ["1.0.0"], DateTimeOffset.UtcNow));

        var evaluator = Substitute.For<IVersionRangeEvaluator>();
        evaluator.SelectBestMatch("[5.0.0,)", Arg.Any<IReadOnlyList<string>>())
            .Returns(new VersionResolutionResult(false, null, 1, "No version satisfies range [5.0.0,)"));

        var resolver = new MultiFeedPackageResolver(
            wrappedOptions, policy,
            Substitute.For<IRemotePackageAcquirer>(),
            enumerator, evaluator,
            NullLogger<MultiFeedPackageResolver>.Instance);

        var request = new PackageRequest("MyPlugin", "[5.0.0,)", "remote", PackageUpdatePolicy.Range, "source");

        await Assert.ThrowsAsync<NoEligibleFeedException>(
            () => resolver.ResolveAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_BoundedRange_SelectsBestMatchWithinRange()
    {
        var options = new FeedResolutionOptions();
        options.Feeds.Add(new("remote", new("https://feed.example/v3/index.json")));
        var wrappedOptions = new OptionsWrapper<FeedResolutionOptions>(options);
        var policy = new FeedResolutionPolicy(wrappedOptions);

        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        enumerator.EnumerateVersionsAsync(Arg.Any<FeedDefinition>(), "MyPlugin", Arg.Any<CancellationToken>())
            .Returns(new PackageVersionList("MyPlugin", "remote", ["1.0.0", "1.5.0", "2.0.0", "3.0.0"], DateTimeOffset.UtcNow));

        var evaluator = Substitute.For<IVersionRangeEvaluator>();
        evaluator.SelectBestMatch("[1.0.0, 2.0.0)", Arg.Any<IReadOnlyList<string>>())
            .Returns(new VersionResolutionResult(true, "1.5.0", 4, null));

        var acquirer = Substitute.For<IRemotePackageAcquirer>();
        acquirer.AcquireAsync(Arg.Any<FeedDefinition>(), "MyPlugin", "1.5.0", Arg.Any<CancellationToken>())
            .Returns("/installed/MyPlugin/1.5.0");

        var resolver = new MultiFeedPackageResolver(
            wrappedOptions, policy, acquirer, enumerator, evaluator,
            NullLogger<MultiFeedPackageResolver>.Instance);

        var request = new PackageRequest("MyPlugin", "[1.0.0, 2.0.0)", "remote", PackageUpdatePolicy.Range, "source");
        var result = await resolver.ResolveAsync(request, CancellationToken.None);

        Assert.Equal("1.5.0", result.Version);
    }

    [Fact]
    public async Task ResolveAsync_ExactVersion_ResolvesExactMatch()
    {
        var options = new FeedResolutionOptions();
        options.Feeds.Add(new("remote", new("https://feed.example/v3/index.json")));
        var wrappedOptions = new OptionsWrapper<FeedResolutionOptions>(options);
        var policy = new FeedResolutionPolicy(wrappedOptions);

        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        enumerator.EnumerateVersionsAsync(Arg.Any<FeedDefinition>(), "MyPlugin", Arg.Any<CancellationToken>())
            .Returns(new PackageVersionList("MyPlugin", "remote", ["1.0.0", "2.0.0", "3.0.0"], DateTimeOffset.UtcNow));

        var evaluator = Substitute.For<IVersionRangeEvaluator>();
        evaluator.SelectBestMatch("[2.0.0]", Arg.Any<IReadOnlyList<string>>())
            .Returns(new VersionResolutionResult(true, "2.0.0", 3, null));

        var acquirer = Substitute.For<IRemotePackageAcquirer>();
        acquirer.AcquireAsync(Arg.Any<FeedDefinition>(), "MyPlugin", "2.0.0", Arg.Any<CancellationToken>())
            .Returns("/installed/MyPlugin/2.0.0");

        var resolver = new MultiFeedPackageResolver(
            wrappedOptions, policy, acquirer, enumerator, evaluator,
            NullLogger<MultiFeedPackageResolver>.Instance);

        var request = new PackageRequest("MyPlugin", "[2.0.0]", "remote", PackageUpdatePolicy.Range, "source");
        var result = await resolver.ResolveAsync(request, CancellationToken.None);

        Assert.Equal("2.0.0", result.Version);
    }

    [Fact]
    public async Task ResolveAsync_ExactVersion_LocalFeedHit_DoesNotEnumerateRemote()
    {
        using var localPackages = new TempDirectory();
        NupkgTestBuilder.Create("MyPlugin", "2.0.0").BuildTo(localPackages.Path);

        var options = new FeedResolutionOptions();
        options.Feeds.Add(new("remote", new("https://feed.example/v3/index.json")));
        options.Feeds.Add(new("local-cache", new Uri(localPackages.Path + Path.DirectorySeparatorChar)));
        options.SetPriority("remote", 0);
        options.SetPriority("local-cache", 100);
        var wrappedOptions = new OptionsWrapper<FeedResolutionOptions>(options);
        var policy = new FeedResolutionPolicy(wrappedOptions);

        var acquirer = Substitute.For<IRemotePackageAcquirer>();
        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        var resolver = new MultiFeedPackageResolver(
            wrappedOptions,
            policy,
            acquirer,
            enumerator,
            Substitute.For<IVersionRangeEvaluator>(),
            NullLogger<MultiFeedPackageResolver>.Instance);

        var request = new PackageRequest("MyPlugin", "[2.0.0]", null, PackageUpdatePolicy.Exact, "source");
        var result = await resolver.ResolveAsync(request, CancellationToken.None);

        Assert.Equal("local-cache", result.FeedName);
        Assert.Equal("2.0.0", result.Version);
        Assert.True(Directory.Exists(result.InstallPath));
        await enumerator.DidNotReceiveWithAnyArgs().EnumerateVersionsAsync(default!, default!, default);
        await acquirer.DidNotReceiveWithAnyArgs().AcquireAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task ResolveAsync_ExactVersion_RemoteDirectAcquire_DoesNotEnumerateVersions()
    {
        var options = new FeedResolutionOptions();
        options.Feeds.Add(new("remote", new("https://feed.example/v3/index.json")));
        var wrappedOptions = new OptionsWrapper<FeedResolutionOptions>(options);
        var policy = new FeedResolutionPolicy(wrappedOptions);

        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        var evaluator = Substitute.For<IVersionRangeEvaluator>();
        var acquirer = Substitute.For<IRemotePackageAcquirer>();
        acquirer.AcquireAsync(Arg.Any<FeedDefinition>(), "MyPlugin", "2.0.0", Arg.Any<CancellationToken>())
            .Returns("/installed/MyPlugin/2.0.0");

        var resolver = new MultiFeedPackageResolver(
            wrappedOptions,
            policy,
            acquirer,
            enumerator,
            evaluator,
            NullLogger<MultiFeedPackageResolver>.Instance);

        var request = new PackageRequest("MyPlugin", "[2.0.0]", null, PackageUpdatePolicy.Exact, "source");
        var result = await resolver.ResolveAsync(request, CancellationToken.None);

        Assert.Equal("remote", result.FeedName);
        Assert.Equal("2.0.0", result.Version);
        await acquirer.Received(1).AcquireAsync(Arg.Any<FeedDefinition>(), "MyPlugin", "2.0.0", Arg.Any<CancellationToken>());
        await enumerator.DidNotReceiveWithAnyArgs().EnumerateVersionsAsync(default!, default!, default);
        evaluator.DidNotReceiveWithAnyArgs().SelectBestMatch(default!, default!);
    }

    [Fact]
    public async Task ResolveAsync_OfflineMode_LocalMiss_DoesNotCallRemote()
    {
        using var localPackages = new TempDirectory();
        var options = new FeedResolutionOptions
        {
            OfflineMode = true
        };
        options.Feeds.Add(new("remote", new("https://feed.example/v3/index.json")));
        options.Feeds.Add(new("remote-secondary", new("https://secondary.example/v3/index.json")));
        options.Feeds.Add(new("local-cache", new Uri(localPackages.Path + Path.DirectorySeparatorChar)));
        var wrappedOptions = new OptionsWrapper<FeedResolutionOptions>(options);
        var policy = new FeedResolutionPolicy(wrappedOptions);

        var acquirer = Substitute.For<IRemotePackageAcquirer>();
        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        var resolver = new MultiFeedPackageResolver(
            wrappedOptions,
            policy,
            acquirer,
            enumerator,
            Substitute.For<IVersionRangeEvaluator>(),
            NullLogger<MultiFeedPackageResolver>.Instance);

        var request = new PackageRequest("MyPlugin", "[2.0.0]", null, PackageUpdatePolicy.Exact, "source");

        await Assert.ThrowsAsync<NoEligibleFeedException>(
            () => resolver.ResolveAsync(request, CancellationToken.None));

        Assert.True(resolver.TryGetDecision("MyPlugin", out var decision));
        Assert.Equal("exact-local-cache-miss+remote-disabled-by-offline-mode", decision.DecisionPath);
        Assert.Contains("was not found in local directory feed", decision.FailureReason);
        Assert.Contains("Offline mode is enabled", decision.FailureReason);
        Assert.Equal(
            decision.FailureReason!.IndexOf("Offline mode is enabled", StringComparison.Ordinal),
            decision.FailureReason.LastIndexOf("Offline mode is enabled", StringComparison.Ordinal));
        await enumerator.DidNotReceiveWithAnyArgs().EnumerateVersionsAsync(default!, default!, default);
        await acquirer.DidNotReceiveWithAnyArgs().AcquireAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task ResolveAsync_RemoteFallbackModeNever_LocalMiss_FailsWithPolicyDecision()
    {
        using var localPackages = new TempDirectory();
        var options = new FeedResolutionOptions
        {
            RemoteFallbackMode = RemoteFallbackMode.Never
        };
        options.Feeds.Add(new("remote", new("https://feed.example/v3/index.json")));
        options.Feeds.Add(new("local-cache", new Uri(localPackages.Path + Path.DirectorySeparatorChar)));
        var wrappedOptions = new OptionsWrapper<FeedResolutionOptions>(options);
        var policy = new FeedResolutionPolicy(wrappedOptions);

        var acquirer = Substitute.For<IRemotePackageAcquirer>();
        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        var resolver = new MultiFeedPackageResolver(
            wrappedOptions,
            policy,
            acquirer,
            enumerator,
            Substitute.For<IVersionRangeEvaluator>(),
            NullLogger<MultiFeedPackageResolver>.Instance);

        var request = new PackageRequest("MyPlugin", "[2.0.0]", null, PackageUpdatePolicy.Exact, "source");

        await Assert.ThrowsAsync<NoEligibleFeedException>(
            () => resolver.ResolveAsync(request, CancellationToken.None));

        Assert.True(resolver.TryGetDecision("MyPlugin", out var decision));
        Assert.Equal("exact-local-cache-miss+remote-disabled-by-policy", decision.DecisionPath);
        Assert.Contains("was not found in local directory feed", decision.FailureReason);
        Assert.Contains("Remote fallback mode is Never", decision.FailureReason);
        await enumerator.DidNotReceiveWithAnyArgs().EnumerateVersionsAsync(default!, default!, default);
        await acquirer.DidNotReceiveWithAnyArgs().AcquireAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task ResolveAsync_Range_RemoteStillEnumeratesVersions()
    {
        var options = new FeedResolutionOptions();
        options.Feeds.Add(new("remote", new("https://feed.example/v3/index.json")));
        var wrappedOptions = new OptionsWrapper<FeedResolutionOptions>(options);
        var policy = new FeedResolutionPolicy(wrappedOptions);

        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        enumerator.EnumerateVersionsAsync(Arg.Any<FeedDefinition>(), "MyPlugin", Arg.Any<CancellationToken>())
            .Returns(new PackageVersionList("MyPlugin", "remote", ["1.0.0", "1.5.0", "2.0.0"], DateTimeOffset.UtcNow));

        var evaluator = Substitute.For<IVersionRangeEvaluator>();
        evaluator.SelectBestMatch("[1.0.0, 2.0.0)", Arg.Any<IReadOnlyList<string>>())
            .Returns(new VersionResolutionResult(true, "1.5.0", 3, null));

        var acquirer = Substitute.For<IRemotePackageAcquirer>();
        acquirer.AcquireAsync(Arg.Any<FeedDefinition>(), "MyPlugin", "1.5.0", Arg.Any<CancellationToken>())
            .Returns("/installed/MyPlugin/1.5.0");

        var resolver = new MultiFeedPackageResolver(
            wrappedOptions,
            policy,
            acquirer,
            enumerator,
            evaluator,
            NullLogger<MultiFeedPackageResolver>.Instance);

        var request = new PackageRequest("MyPlugin", "[1.0.0, 2.0.0)", null, PackageUpdatePolicy.Range, "source");
        var result = await resolver.ResolveAsync(request, CancellationToken.None);

        Assert.Equal("1.5.0", result.Version);
        await enumerator.Received(1).EnumerateVersionsAsync(Arg.Any<FeedDefinition>(), "MyPlugin", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_BareVersion_UsesRemoteDirectAcquireFastPath()
    {
        var options = new FeedResolutionOptions();
        options.Feeds.Add(new("remote", new("https://feed.example/v3/index.json")));
        var wrappedOptions = new OptionsWrapper<FeedResolutionOptions>(options);
        var policy = new FeedResolutionPolicy(wrappedOptions);

        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        enumerator.EnumerateVersionsAsync(Arg.Any<FeedDefinition>(), "MyPlugin", Arg.Any<CancellationToken>())
            .Returns(new PackageVersionList("MyPlugin", "remote", ["1.0.0", "2.0.0"], DateTimeOffset.UtcNow));

        var evaluator = Substitute.For<IVersionRangeEvaluator>();
        evaluator.SelectBestMatch("2.0.0", Arg.Any<IReadOnlyList<string>>())
            .Returns(new VersionResolutionResult(true, "2.0.0", 2, null));

        var acquirer = Substitute.For<IRemotePackageAcquirer>();
        acquirer.AcquireAsync(Arg.Any<FeedDefinition>(), "MyPlugin", "2.0.0", Arg.Any<CancellationToken>())
            .Returns("/installed/MyPlugin/2.0.0");

        var resolver = new MultiFeedPackageResolver(
            wrappedOptions, policy, acquirer, enumerator, evaluator,
            NullLogger<MultiFeedPackageResolver>.Instance);

        var request = new PackageRequest("MyPlugin", "2.0.0", "remote", PackageUpdatePolicy.Range, "source");
        var result = await resolver.ResolveAsync(request, CancellationToken.None);

        Assert.Equal("2.0.0", result.Version);
        await enumerator.DidNotReceive().EnumerateVersionsAsync(
            Arg.Any<FeedDefinition>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        evaluator.DidNotReceive().SelectBestMatch(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>());
    }

    [Fact]
    public async Task ResolveAsync_DependencyRequest_SelectsLowestSatisfyingVersion()
    {
        var options = new FeedResolutionOptions();
        options.Feeds.Add(new("remote", new("https://feed.example/v3/index.json")));
        var wrappedOptions = new OptionsWrapper<FeedResolutionOptions>(options);
        var policy = new FeedResolutionPolicy(wrappedOptions);

        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        enumerator.EnumerateVersionsAsync(Arg.Any<FeedDefinition>(), "Microsoft.EntityFrameworkCore", Arg.Any<CancellationToken>())
            .Returns(new PackageVersionList(
                "Microsoft.EntityFrameworkCore",
                "remote",
                ["10.0.3", "10.0.4", "11.0.0-preview.4.26230.115"],
                DateTimeOffset.UtcNow));

        var evaluator = Substitute.For<IVersionRangeEvaluator>();

        var acquirer = Substitute.For<IRemotePackageAcquirer>();
        acquirer.AcquireAsync(Arg.Any<FeedDefinition>(), "Microsoft.EntityFrameworkCore", "10.0.3", Arg.Any<CancellationToken>())
            .Returns("/installed/Microsoft.EntityFrameworkCore/10.0.3");

        var resolver = new MultiFeedPackageResolver(
            wrappedOptions, policy, acquirer, enumerator, evaluator,
            NullLogger<MultiFeedPackageResolver>.Instance);

        var request = new PackageRequest("Microsoft.EntityFrameworkCore", "[10.0.3,)", "remote", PackageUpdatePolicy.Exact, "dependency-of:Plugin.Root");
        var result = await resolver.ResolveAsync(request, CancellationToken.None);

        Assert.Equal("10.0.3", result.Version);
        evaluator.DidNotReceiveWithAnyArgs().SelectBestMatch(default!, default!);
    }

    [Fact]
    public async Task ResolveAsync_DependencyRequest_PreservesFeedAdvertisedVersionString()
    {
        var options = new FeedResolutionOptions();
        options.Feeds.Add(new("remote", new("https://feed.example/v3/index.json")));
        var wrappedOptions = new OptionsWrapper<FeedResolutionOptions>(options);
        var policy = new FeedResolutionPolicy(wrappedOptions);

        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        enumerator.EnumerateVersionsAsync(Arg.Any<FeedDefinition>(), "Plugin.Dependency", Arg.Any<CancellationToken>())
            .Returns(new PackageVersionList(
                "Plugin.Dependency",
                "remote",
                ["1.0", "1.0.1"],
                DateTimeOffset.UtcNow));

        var evaluator = Substitute.For<IVersionRangeEvaluator>();

        var acquirer = Substitute.For<IRemotePackageAcquirer>();
        acquirer.AcquireAsync(Arg.Any<FeedDefinition>(), "Plugin.Dependency", "1.0", Arg.Any<CancellationToken>())
            .Returns("/installed/Plugin.Dependency/1.0");

        var resolver = new MultiFeedPackageResolver(
            wrappedOptions, policy, acquirer, enumerator, evaluator,
            NullLogger<MultiFeedPackageResolver>.Instance);

        var request = new PackageRequest("Plugin.Dependency", "[1.0.0,)", "remote", PackageUpdatePolicy.Exact, "dependency-of:Plugin.Root");
        var result = await resolver.ResolveAsync(request, CancellationToken.None);

        Assert.Equal("1.0", result.Version);
        await acquirer.Received(1).AcquireAsync(Arg.Any<FeedDefinition>(), "Plugin.Dependency", "1.0", Arg.Any<CancellationToken>());
        evaluator.DidNotReceiveWithAnyArgs().SelectBestMatch(default!, default!);
    }

    [Fact]
    public async Task ResolveAsync_NoMatch_DecisionContainsDiagnostic()
    {
        var options = new FeedResolutionOptions();
        options.Feeds.Add(new("remote", new("https://feed.example/v3/index.json")));
        var wrappedOptions = new OptionsWrapper<FeedResolutionOptions>(options);
        var policy = new FeedResolutionPolicy(wrappedOptions);

        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        enumerator.EnumerateVersionsAsync(Arg.Any<FeedDefinition>(), "MyPlugin", Arg.Any<CancellationToken>())
            .Returns(new PackageVersionList("MyPlugin", "remote", ["1.0.0"], DateTimeOffset.UtcNow));

        var evaluator = Substitute.For<IVersionRangeEvaluator>();
        evaluator.SelectBestMatch("[5.0.0,)", Arg.Any<IReadOnlyList<string>>())
            .Returns(new VersionResolutionResult(false, null, 1, "No version satisfies range [5.0.0,)"));

        var resolver = new MultiFeedPackageResolver(
            wrappedOptions, policy,
            Substitute.For<IRemotePackageAcquirer>(),
            enumerator, evaluator,
            NullLogger<MultiFeedPackageResolver>.Instance);

        var request = new PackageRequest("MyPlugin", "[5.0.0,)", "remote", PackageUpdatePolicy.Range, "source");

        await Assert.ThrowsAsync<NoEligibleFeedException>(
            () => resolver.ResolveAsync(request, CancellationToken.None));

        Assert.True(resolver.TryGetDecision("MyPlugin", out var decision));
        Assert.False(decision.FeedUnavailable);
        Assert.Equal("remote", decision.SelectedFeed);
        Assert.Equal(1, decision.EnumeratedVersionCount);
        Assert.Equal("No version satisfies range [5.0.0,)", decision.FailureReason);
    }

    [Fact]
    public async Task ResolveAsync_RemoteFeed_UsesCacheHitMetadataInDecision()
    {
        var options = new FeedResolutionOptions();
        options.Feeds.Add(new("remote", new("https://feed.example/v3/index.json")));
        var wrappedOptions = new OptionsWrapper<FeedResolutionOptions>(options);
        var policy = new FeedResolutionPolicy(wrappedOptions);

        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        enumerator.EnumerateVersionsAsync(Arg.Any<FeedDefinition>(), "MyPlugin", Arg.Any<CancellationToken>())
            .Returns(new PackageVersionList("MyPlugin", "remote", ["1.0.0", "2.0.0"], DateTimeOffset.UtcNow, CacheHit: true));

        var evaluator = Substitute.For<IVersionRangeEvaluator>();
        evaluator.SelectBestMatch("", Arg.Any<IReadOnlyList<string>>())
            .Returns(new VersionResolutionResult(true, "2.0.0", 2, null));

        var acquirer = Substitute.For<IRemotePackageAcquirer>();
        acquirer.AcquireAsync(Arg.Any<FeedDefinition>(), "MyPlugin", "2.0.0", Arg.Any<CancellationToken>())
            .Returns("/installed/MyPlugin/2.0.0");

        var resolver = new MultiFeedPackageResolver(
            wrappedOptions, policy, acquirer, enumerator, evaluator,
            NullLogger<MultiFeedPackageResolver>.Instance);

        var request = new PackageRequest("MyPlugin", "", "remote", PackageUpdatePolicy.Range, "source");
        await resolver.ResolveAsync(request, CancellationToken.None);

        Assert.True(resolver.TryGetDecision("MyPlugin", out var decision));
        Assert.True(decision.CacheHit);
    }

    [Fact]
    public async Task ResolveAsync_RemoteFeed_EnumerationException_RecordsFailureDecision()
    {
        var options = new FeedResolutionOptions();
        options.Feeds.Add(new("remote", new("https://feed.example/v3/index.json")));
        var wrappedOptions = new OptionsWrapper<FeedResolutionOptions>(options);
        var policy = new FeedResolutionPolicy(wrappedOptions);

        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        enumerator.EnumerateVersionsAsync(Arg.Any<FeedDefinition>(), "MyPlugin", Arg.Any<CancellationToken>())
            .Returns<Task<PackageVersionList>>(_ => throw new InvalidOperationException("Feed timeout"));

        var resolver = new MultiFeedPackageResolver(
            wrappedOptions,
            policy,
            Substitute.For<IRemotePackageAcquirer>(),
            enumerator,
            Substitute.For<IVersionRangeEvaluator>(),
            NullLogger<MultiFeedPackageResolver>.Instance);

        var request = new PackageRequest("MyPlugin", "", "remote", PackageUpdatePolicy.Range, "source");

        var exception = await Assert.ThrowsAsync<NoEligibleFeedException>(
            () => resolver.ResolveAsync(request, CancellationToken.None));

        Assert.Contains("Feed timeout", exception.Message, StringComparison.Ordinal);
        Assert.True(resolver.TryGetDecision("MyPlugin", out var decision));
        Assert.Equal("remote", decision.SelectedFeed);
        Assert.Equal("version-enumeration-error", decision.DecisionPath);
        Assert.False(decision.FeedUnavailable);
        Assert.Contains("Feed timeout", decision.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_LocalFeed_WithoutExplicitVersion_ThrowsClearError()
    {
        var options = new FeedResolutionOptions();
        options.Feeds.Add(new("local-drop", new("file:///packages/local")));
        var wrappedOptions = new OptionsWrapper<FeedResolutionOptions>(options);
        var policy = new FeedResolutionPolicy(wrappedOptions);

        var resolver = new MultiFeedPackageResolver(
            wrappedOptions,
            policy,
            Substitute.For<IRemotePackageAcquirer>(),
            Substitute.For<IFeedVersionEnumerator>(),
            Substitute.For<IVersionRangeEvaluator>(),
            NullLogger<MultiFeedPackageResolver>.Instance);

        var request = new PackageRequest("MyPlugin", "", "local-drop", PackageUpdatePolicy.Range, "source");

        var exception = await Assert.ThrowsAsync<NoEligibleFeedException>(
            () => resolver.ResolveAsync(request, CancellationToken.None));

        Assert.Contains("requires an explicit version", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(resolver.TryGetDecision("MyPlugin", out var decision));
        Assert.Equal("local-drop", decision.SelectedFeed);
        Assert.False(decision.FeedUnavailable);
    }

    [Fact]
    public async Task ResolveAsync_Reconciliation_NewerVersionWithinRange_ResolvesToNewer()
    {
        var options = new FeedResolutionOptions();
        options.Feeds.Add(new("remote", new("https://feed.example/v3/index.json")));
        var wrappedOptions = new OptionsWrapper<FeedResolutionOptions>(options);
        var policy = new FeedResolutionPolicy(wrappedOptions);

        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        var evaluator = Substitute.For<IVersionRangeEvaluator>();
        var acquirer = Substitute.For<IRemotePackageAcquirer>();
        acquirer.AcquireAsync(Arg.Any<FeedDefinition>(), "MyPlugin", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => $"/installed/MyPlugin/{ci.ArgAt<string>(2)}");

        // First resolution: feed has 1.0.0 and 1.5.0
        enumerator.EnumerateVersionsAsync(Arg.Any<FeedDefinition>(), "MyPlugin", Arg.Any<CancellationToken>())
            .Returns(
                new PackageVersionList("MyPlugin", "remote", ["1.0.0", "1.5.0"], DateTimeOffset.UtcNow),
                new PackageVersionList("MyPlugin", "remote", ["1.0.0", "1.5.0", "1.9.0"], DateTimeOffset.UtcNow));

        evaluator.SelectBestMatch("[1.0.0, 2.0.0)", Arg.Any<IReadOnlyList<string>>())
            .Returns(
                new VersionResolutionResult(true, "1.5.0", 2, null),
                new VersionResolutionResult(true, "1.9.0", 3, null));

        var resolver = new MultiFeedPackageResolver(
            wrappedOptions, policy, acquirer, enumerator, evaluator,
            NullLogger<MultiFeedPackageResolver>.Instance);

        var request = new PackageRequest("MyPlugin", "[1.0.0, 2.0.0)", "remote", PackageUpdatePolicy.Range, "source");

        var first = await resolver.ResolveAsync(request, CancellationToken.None);
        Assert.Equal("1.5.0", first.Version);

        // Second resolution (re-reconciliation): feed now has 1.9.0
        var second = await resolver.ResolveAsync(request, CancellationToken.None);
        Assert.Equal("1.9.0", second.Version);
    }

    [Fact]
    public async Task ResolveAsync_Reconciliation_NewVersionOutsideRange_VersionUnchanged()
    {
        var options = new FeedResolutionOptions();
        options.Feeds.Add(new("remote", new("https://feed.example/v3/index.json")));
        var wrappedOptions = new OptionsWrapper<FeedResolutionOptions>(options);
        var policy = new FeedResolutionPolicy(wrappedOptions);

        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        var evaluator = Substitute.For<IVersionRangeEvaluator>();
        var acquirer = Substitute.For<IRemotePackageAcquirer>();
        acquirer.AcquireAsync(Arg.Any<FeedDefinition>(), "MyPlugin", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => $"/installed/MyPlugin/{ci.ArgAt<string>(2)}");

        // Both resolutions: range stays [1.0.0, 2.0.0), best match is always 1.5.0
        enumerator.EnumerateVersionsAsync(Arg.Any<FeedDefinition>(), "MyPlugin", Arg.Any<CancellationToken>())
            .Returns(
                new PackageVersionList("MyPlugin", "remote", ["1.0.0", "1.5.0"], DateTimeOffset.UtcNow),
                new PackageVersionList("MyPlugin", "remote", ["1.0.0", "1.5.0", "3.0.0"], DateTimeOffset.UtcNow));

        evaluator.SelectBestMatch("[1.0.0, 2.0.0)", Arg.Any<IReadOnlyList<string>>())
            .Returns(
                new VersionResolutionResult(true, "1.5.0", 2, null),
                new VersionResolutionResult(true, "1.5.0", 3, null));

        var resolver = new MultiFeedPackageResolver(
            wrappedOptions, policy, acquirer, enumerator, evaluator,
            NullLogger<MultiFeedPackageResolver>.Instance);

        var request = new PackageRequest("MyPlugin", "[1.0.0, 2.0.0)", "remote", PackageUpdatePolicy.Range, "source");

        var first = await resolver.ResolveAsync(request, CancellationToken.None);
        Assert.Equal("1.5.0", first.Version);

        // 3.0.0 appeared but is outside range — version stays at 1.5.0
        var second = await resolver.ResolveAsync(request, CancellationToken.None);
        Assert.Equal("1.5.0", second.Version);
    }

    [Fact]
    public async Task ResolveAsync_Reconciliation_LatestNoRange_UpdatesToNewer()
    {
        var options = new FeedResolutionOptions();
        options.Feeds.Add(new("remote", new("https://feed.example/v3/index.json")));
        var wrappedOptions = new OptionsWrapper<FeedResolutionOptions>(options);
        var policy = new FeedResolutionPolicy(wrappedOptions);

        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        var evaluator = Substitute.For<IVersionRangeEvaluator>();
        var acquirer = Substitute.For<IRemotePackageAcquirer>();
        acquirer.AcquireAsync(Arg.Any<FeedDefinition>(), "MyPlugin", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => $"/installed/MyPlugin/{ci.ArgAt<string>(2)}");

        // Empty range = latest. Feed gets a new version between calls.
        enumerator.EnumerateVersionsAsync(Arg.Any<FeedDefinition>(), "MyPlugin", Arg.Any<CancellationToken>())
            .Returns(
                new PackageVersionList("MyPlugin", "remote", ["1.0.0", "2.0.0"], DateTimeOffset.UtcNow),
                new PackageVersionList("MyPlugin", "remote", ["1.0.0", "2.0.0", "3.0.0"], DateTimeOffset.UtcNow));

        evaluator.SelectBestMatch("", Arg.Any<IReadOnlyList<string>>())
            .Returns(
                new VersionResolutionResult(true, "2.0.0", 2, null),
                new VersionResolutionResult(true, "3.0.0", 3, null));

        var resolver = new MultiFeedPackageResolver(
            wrappedOptions, policy, acquirer, enumerator, evaluator,
            NullLogger<MultiFeedPackageResolver>.Instance);

        var request = new PackageRequest("MyPlugin", "", "remote", PackageUpdatePolicy.Range, "source");

        var first = await resolver.ResolveAsync(request, CancellationToken.None);
        Assert.Equal("2.0.0", first.Version);

        var second = await resolver.ResolveAsync(request, CancellationToken.None);
        Assert.Equal("3.0.0", second.Version);
    }
}
