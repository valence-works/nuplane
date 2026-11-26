using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Nuplane.Abstractions;
using Nuplane.Feeds;
using Nuplane.Feeds.Policy;
using Nuplane.Feeds.Versioning;
using Nuplane.Integration.Tests.Restore;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class FeedTrustReconciliationTests : IDisposable
{
    private const string FeedName = "trust-feed";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nuplane-trust-{Guid.NewGuid():N}");
    private string FeedPath => Path.Combine(_root, "feed");

    [Theory]
    [InlineData(null, "0")]
    [InlineData("Untrusted", FeedName)]
    public async Task ResolveAsync_UntrustedFeed_RefusesBeforeEnumerationOrAcquisition(string? trust, string key)
    {
        var acquirer = Substitute.For<IRemotePackageAcquirer>();
        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        var feedKey = $"Nuplane:Setup:Feeds:{key}";
        var configuration = new Dictionary<string, string?>
        {
            [$"{feedKey}:Name"] = FeedName,
            [$"{feedKey}:ServiceIndex"] = "https://feed.example/v3/index.json",
        };
        if (trust is not null)
        {
            configuration[$"{feedKey}:Trust"] = trust;
        }

        await using var provider = BuildProvider(configuration, services =>
        {
            services.Replace(ServiceDescriptor.Singleton(acquirer));
            services.Replace(ServiceDescriptor.Singleton(enumerator));
        });
        var resolver = provider.GetRequiredService<IPackageResolver>();

        var refusal = await Assert.ThrowsAsync<NoEligibleFeedException>(() =>
            resolver.ResolveAsync(new("Plugin", "[1.0.0,2.0.0)", FeedName, PackageUpdatePolicy.Range, "test"),
                CancellationToken.None));

        Assert.Contains(FeedName, refusal.Message);
        Assert.Contains($"{feedKey}:Trust", refusal.Message);
        Assert.Empty(acquirer.ReceivedCalls());
        Assert.Empty(enumerator.ReceivedCalls());
        Assert.False(Directory.Exists(Path.Combine(_root, "installed")));
    }

    [Fact]
    public async Task TriggerAsync_TrustedFeedBecomesUntrusted_PreservesLastKnownGood()
    {
        var package = HostFreeRestoreTestSupport.WriteNupkg(FeedPath);
        var configuration = DirectoryConfiguration();
        configuration[$"Nuplane:Setup:Feeds:{FeedName}:Trust"] = "Trusted";
        await using (var trusted = BuildProvider(configuration))
        {
            var applied = await ReconcileAsync(trusted);
            Assert.False(applied.IsDegraded);
            Assert.Equal(package.PackageId, (await ReadActiveAsync(trusted)).PackageId);
        }

        configuration[$"Nuplane:Setup:Feeds:{FeedName}:Trust"] = "Untrusted";
        await using var untrusted = BuildProvider(configuration);

        var refused = await ReconcileAsync(untrusted);

        Assert.True(refused.IsDegraded);
        Assert.Contains(package.PackageId, refused.FailedPackages, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(package.PackageId, (await ReadActiveAsync(untrusted)).PackageId);
    }

    [Fact]
    public async Task TriggerAsync_UntrustedFeedHasPackageOverride_AcquiresOnlyThatPackage()
    {
        var allowed = HostFreeRestoreTestSupport.WriteNupkg(FeedPath);
        var refused = HostFreeRestoreTestSupport.WriteNupkg(FeedPath);
        var configuration = DirectoryConfiguration();
        const string overridePath = "Nuplane:FeedResolution:TrustOverrides:0";
        configuration[$"{overridePath}:FeedName"] = FeedName;
        configuration[$"{overridePath}:PackageId"] = allowed.PackageId;
        configuration[$"{overridePath}:Reason"] = "Package verified independently for this rollout.";
        await using var provider = BuildProvider(configuration);

        var result = await ReconcileAsync(provider);

        Assert.True(result.IsDegraded);
        Assert.Equal(refused.PackageId, Assert.Single(result.FailedPackages), ignoreCase: true);
        Assert.Equal(allowed.PackageId, (await ReadActiveAsync(provider)).PackageId);
        Assert.False(Directory.Exists(refused.InstallDirectory(Path.Combine(_root, "installed"), FeedName)));
    }

    private Dictionary<string, string?> DirectoryConfiguration() =>
        new()
        {
            [$"Nuplane:Setup:Feeds:{FeedName}:DirectoryPath"] = FeedPath,
            [$"Nuplane:Setup:Feeds:{FeedName}:Directory:Watch"] = "false",
            [$"Nuplane:Setup:Feeds:{FeedName}:IncludePatterns:0"] = "*",
        };

    private ServiceProvider BuildProvider(Dictionary<string, string?> values, Action<IServiceCollection>? configure = null)
    {
        values["Nuplane:FeedResolution:PackageInstallRoot"] = Path.Combine(_root, "installed");
        values["Nuplane:LockFile:Path"] = Path.Combine(_root, "nuplane.lock.json");
        values["Nuplane:StoreRegistry:StateFilePath"] = Path.Combine(_root, "state", "store-state.json");
        values["Nuplane:Reconciliation:MaxRetryAttempts"] = "0";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection("Nuplane");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(configuration);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static Task<ReconciliationRunResult> ReconcileAsync(ServiceProvider provider) =>
        provider.GetRequiredService<IReconciliationService>()
            .TriggerAsync(ReconciliationTrigger.Manual("feed-trust-test"), CancellationToken.None);

    private static async Task<ActivePackage> ReadActiveAsync(ServiceProvider provider) =>
        Assert.Single((await provider.GetRequiredService<IActivePackageCatalog>()
            .GetActivePackagesAsync(CancellationToken.None)).Packages);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
