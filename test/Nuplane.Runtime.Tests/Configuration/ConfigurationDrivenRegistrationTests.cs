using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Hosting;
using Nuplane.Loading;
using Nuplane.Loading.Hosting.Builder;
using Nuplane.Runtime.Configuration;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Configuration;

public sealed class ConfigurationDrivenRegistrationTests
{
    [Fact]
    public void AddNuplane_FromConfiguration_BindsSetupAndRuntimeOptions()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-config-registration", Guid.NewGuid().ToString("N"));
        var packagesPath = Path.Combine(root, "packages");
        var stateFilePath = Path.Combine(root, "state.json");
        Directory.CreateDirectory(packagesPath);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nuplane:Setup:AutomaticReconciliation"] = "true",
                    ["Nuplane:Setup:PollInterval"] = "00:00:45",
                    ["Nuplane:Setup:StateFilePath"] = stateFilePath,
                    ["Nuplane:Setup:Feeds:0:Name"] = "local-packages",
                    ["Nuplane:Setup:Feeds:0:DirectoryPath"] = packagesPath,
                    ["Nuplane:Setup:Feeds:0:IncludePatterns:0"] = "*",
                    ["Nuplane:Setup:Feeds:0:Directory:Watch"] = "false",
                    ["Nuplane:Setup:Feeds:0:Directory:DebounceWindow"] = "00:00:02",
                    ["Nuplane:Setup:Feeds:1:Name"] = "nuget.org",
                    ["Nuplane:Setup:Feeds:1:ServiceIndex"] = "https://api.nuget.org/v3/index.json",
                    ["Nuplane:Setup:Feeds:1:TrustLevel"] = nameof(FeedTrustLevel.Untrusted),
                    ["Nuplane:Setup:Feeds:1:Credentials"] = "secrets://nuget",
                    ["Nuplane:Setup:Feeds:1:IncludePatterns:0"] = "Elsa.*",
                    ["Nuplane:Reconciliation:MaxRetryAttempts"] = "5",
                    ["Nuplane:StoreRegistry:StateFilePath"] = Path.Combine(root, "ignored-by-setup.json")
                })
                .Build();

            var services = new ServiceCollection();
            services.AddNuplane(configuration.GetSection("Nuplane"));

            using var provider = services.BuildServiceProvider();

            var reconciliation = provider.GetRequiredService<IOptions<ReconciliationOptions>>().Value;
            var storeRegistry = provider.GetRequiredService<IOptions<StoreRegistryOptions>>().Value;
            var feedResolution = provider.GetRequiredService<IOptions<FeedResolutionOptions>>().Value;
            var sourceTrust = provider.GetRequiredService<IOptions<SourceTrustOptions>>().Value;
            var hostedServices = provider.GetServices<IHostedService>().ToArray();

            Assert.True(reconciliation.EnableAutomaticReconciliation);
            Assert.Equal(TimeSpan.FromSeconds(45), reconciliation.PollInterval);
            Assert.Equal(5, reconciliation.MaxRetryAttempts);
            Assert.Equal(stateFilePath, storeRegistry.StateFilePath);

            Assert.Single(hostedServices);
            Assert.IsType<ReconciliationHostedService>(hostedServices[0]);

            var localFeed = Assert.Single(feedResolution.Feeds, feed => feed.Name == "local-packages");
            Assert.Equal(Uri.UriSchemeFile, localFeed.ServiceIndex.Scheme);

            var remoteFeed = Assert.Single(feedResolution.Feeds, feed => feed.Name == "nuget.org");
            Assert.Equal(new Uri("https://api.nuget.org/v3/index.json"), remoteFeed.ServiceIndex);
            Assert.Equal(FeedTrustLevel.Untrusted, remoteFeed.TrustLevel);
            Assert.Equal("secrets://nuget", remoteFeed.Credentials);

            Assert.Contains("local-packages", sourceTrust.AllowedSourceNames);
            Assert.Contains("nuget.org", sourceTrust.AllowedSourceNames);
            Assert.Single(sourceTrust.AllowedPackageIds);
            Assert.Contains("*", sourceTrust.AllowedPackageIds);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort cleanup for temp test content.
            }
        }
    }

    [Fact]
    public void AddNuplane_FromConfiguration_IncludeAllAlias_CollapsesAllowlistToWildcard()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-include-all-config", Guid.NewGuid().ToString("N"));
        var packagesPath = Path.Combine(root, "packages");
        Directory.CreateDirectory(packagesPath);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nuplane:Setup:Feeds:0:Name"] = "drop-folder",
                    ["Nuplane:Setup:Feeds:0:DirectoryPath"] = packagesPath,
                    ["Nuplane:Setup:Feeds:0:IncludeAll"] = "true",
                    ["Nuplane:Setup:Feeds:1:Name"] = "nuget.org",
                    ["Nuplane:Setup:Feeds:1:ServiceIndex"] = "https://api.nuget.org/v3/index.json",
                    ["Nuplane:Setup:Feeds:1:IncludePatterns:0"] = "Elsa.*"
                })
                .Build();

            var services = new ServiceCollection();
            services.AddNuplane(configuration.GetSection("Nuplane"));

            using var provider = services.BuildServiceProvider();

            var sourceTrust = provider.GetRequiredService<IOptions<SourceTrustOptions>>().Value;

            Assert.Single(sourceTrust.AllowedPackageIds);
            Assert.Contains("*", sourceTrust.AllowedPackageIds);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort cleanup for temp test content.
            }
        }
    }

    [Fact]
    public void AutoloadPackages_FromConfiguration_BindsLoadingOptions_AndAllowsCodeOverride()
    {
        var activeStoreRoot = Path.Combine(Path.GetTempPath(), "nuplane-active-store", Guid.NewGuid().ToString("N"));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nuplane:Loading:Enabled"] = "false",
                ["Nuplane:Loading:DeactivationTimeout"] = "00:00:20",
                ["Nuplane:Loading:ActiveStoreRoot"] = activeStoreRoot,
                ["Nuplane:Loading:SharedAssemblies:0:Name"] = "Nuplane.Abstractions",
                ["Nuplane:Loading:SharedAssemblies:0:PublicKeyToken"] = "31bf3856ad364e35",
                ["Nuplane:Loading:SharedAssemblies:0:MajorVersion"] = "1"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddNuplane(configuration.GetSection("Nuplane"), nuplane =>
        {
            nuplane.AutoloadPackages(configuration.GetSection("Nuplane"), load => load.Enable());
        });

        using var provider = services.BuildServiceProvider();

        var loading = provider.GetRequiredService<IOptions<LoadingOptions>>().Value;

        Assert.True(loading.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(20), loading.DeactivationTimeout);
        Assert.Equal(activeStoreRoot, loading.ActiveStoreRoot);

        var sharedAssembly = Assert.Single(loading.SharedAssemblies);
        Assert.Equal("Nuplane.Abstractions", sharedAssembly.Name);
        Assert.Equal("31bf3856ad364e35", sharedAssembly.PublicKeyToken);
        Assert.Equal(1, sharedAssembly.MajorVersion);
    }
}
