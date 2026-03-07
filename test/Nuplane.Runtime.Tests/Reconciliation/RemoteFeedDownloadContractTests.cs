using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.FeedPolicy;
using Nuplane.Runtime.Tests.TestSupport;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class RemoteFeedDownloadContractTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nuplane-remote-feed-test-{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Resolve_RemoteFeed_DownloadsAndExtractsPackage()
    {
        var packageBytes = NupkgTestBuilder.Create("MyPlugin", "1.0.0").Build();
        await using var server = new TestNuGetFeedServer("MyPlugin", "1.0.0", packageBytes);

        var options = new FeedResolutionOptions { PackageInstallRoot = Path.Combine(_tempDir, "installed") };
        options.Feeds.Add(new("remote-feed", server.ServiceIndexUri, FeedTrustLevel.Trusted));
        var policy = new FeedResolutionPolicy(new OptionsWrapper<FeedResolutionOptions>(options));
        var resolver = new MultiFeedPackageResolver(new OptionsWrapper<FeedResolutionOptions>(options), policy);

        var result = await resolver.ResolveAsync(
            new("MyPlugin", "1.0.0", "remote-feed", PackageUpdatePolicy.Exact, "remote-source"),
            CancellationToken.None);

        Assert.Equal("remote-feed", result.FeedName);
        Assert.True(Directory.Exists(result.InstallPath));
        Assert.True(File.Exists(Path.Combine(result.InstallPath, "MyPlugin.nuspec")));
        Assert.Equal(1, server.PackageDownloads);
    }

    [Fact]
    public async Task Resolve_RemoteFeed_ReusesExtractedInstallPathOnSubsequentResolve()
    {
        var packageBytes = NupkgTestBuilder.Create("MyPlugin", "1.0.0").Build();
        await using var server = new TestNuGetFeedServer("MyPlugin", "1.0.0", packageBytes);

        var options = new FeedResolutionOptions { PackageInstallRoot = Path.Combine(_tempDir, "installed") };
        options.Feeds.Add(new("remote-feed", server.ServiceIndexUri, FeedTrustLevel.Trusted));
        var policy = new FeedResolutionPolicy(new OptionsWrapper<FeedResolutionOptions>(options));
        var resolver = new MultiFeedPackageResolver(new OptionsWrapper<FeedResolutionOptions>(options), policy);
        var request = new PackageRequest("MyPlugin", "1.0.0", "remote-feed", PackageUpdatePolicy.Exact, "remote-source");

        var first = await resolver.ResolveAsync(request, CancellationToken.None);
        var second = await resolver.ResolveAsync(request, CancellationToken.None);

        Assert.Equal(first.InstallPath, second.InstallPath);
        Assert.Equal(1, server.PackageDownloads);
    }
}

