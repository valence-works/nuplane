using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nuplane.Hosting;
using Nuplane.Loading;
using Nuplane.Loading.Hosting.Builder;
using Nuplane.Runtime.Configuration;

namespace Nuplane.Runtime.Tests.Configuration;

public sealed class ConfigurationDrivenRegistrationTests
{
    [Fact]
    public void AddNuplane_FromConfiguration_RegistersAutomaticSchedulerAndDispatcher()
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
                    ["Nuplane:Reconciliation:MaxRetryAttempts"] = "5",
                    ["Nuplane:StoreRegistry:StateFilePath"] = Path.Combine(root, "ignored-by-setup.json")
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(configuration.GetSection("Nuplane"));

            var hostedServiceTypes = services
                .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
                .Select(descriptor => descriptor.ImplementationType)
                .Where(static type => type is not null)
                .ToArray();

            Assert.Equal(2, hostedServiceTypes.Length);
            Assert.Contains(typeof(ReconciliationHostedService), hostedServiceTypes);
            Assert.Contains(typeof(ReconciliationTriggerDispatcherHostedService), hostedServiceTypes);
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
    public void AddNuplane_FromConfiguration_WithoutFilters_DoesNotAllowlistAnyPackages()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-no-filter-config", Guid.NewGuid().ToString("N"));
        var packagesPath = Path.Combine(root, "packages");
        Directory.CreateDirectory(packagesPath);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nuplane:Setup:Feeds:0:Name"] = "drop-folder",
                    ["Nuplane:Setup:Feeds:0:DirectoryPath"] = packagesPath
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(configuration.GetSection("Nuplane"));

            using var provider = services.BuildServiceProvider();

            var sourceTrust = provider.GetRequiredService<IOptions<SourceTrustOptions>>().Value;

            Assert.Empty(sourceTrust.AllowedPackageIds);
            Assert.Contains("drop-folder", sourceTrust.AllowedSourceNames);
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
            services.AddLogging();
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
        services.AddLogging();
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
