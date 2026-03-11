using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Loading;
using Nuplane.Loading.Hosting.Builder;
using Nuplane.Loading.Registration;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Sources.Directory;
using Nuplane.Sources.Directory.Builder;

namespace Nuplane.Integration.Tests.Reconciliation;

/// <summary>
/// Baseline observability compatibility tests proving that module registration
/// changes do not break core runtime resolution or observability invariants.
/// </summary>
public sealed class ModuleRegistrationCompatibilityTests
{
    [Fact]
    public void LoadingRegistration_RegistersRequiredServices()
    {
        var services = new ServiceCollection();

        LoadingRegistrationServices.Register(services);

        Assert.Contains(services, d => d.ServiceType == typeof(IPackageLoader));
        Assert.Contains(services, d => d.ServiceType == typeof(IPackageUnloadCoordinator));
        Assert.Contains(services, d => d.ServiceType == typeof(IPackageTypeScanner));
        Assert.Contains(services, d => d.ServiceType == typeof(LoadingOptionsValidator));
    }

    [Fact]
    public void LoadingRegistration_IdempotentWhenCalledMultipleTimes()
    {
        var services = new ServiceCollection();

        LoadingRegistrationServices.Register(services);
        LoadingRegistrationServices.Register(services);

        // TryAdd semantics should prevent duplicate registrations
        Assert.Equal(1, services.Count(d =>
            d.ServiceType == typeof(IPackageLoader)
            && d.Lifetime == ServiceLifetime.Singleton));
        Assert.Equal(1, services.Count(d =>
            d.ServiceType == typeof(IPackageUnloadCoordinator)
            && d.Lifetime == ServiceLifetime.Singleton));
    }

    [Fact]
    public async Task CoreRuntime_WithLoadingOptions_ReconcilesCycleWithoutErrors()
    {
        var service = ReconciliationServiceFactory.Create(
            loadingOptions: new LoadingOptions { Enabled = false });

        var result = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.FailedPackages);
    }

    [Fact]
    public async Task CoreRuntime_WithoutLoadingOptions_ReconcilesCycleWithoutErrors()
    {
        var service = ReconciliationServiceFactory.Create();

        var result = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.FailedPackages);
    }

    [Fact]
    public void LoadingDirectRegistration_DoesNotConflictWithCoreServices()
    {
        var services = new ServiceCollection();

        // Register loading module directly (no builder)
        LoadingRegistrationServices.Register(services);

        // Verify loading services are present alongside DI container
        Assert.Contains(services, d => d.ServiceType == typeof(IPackageLoader));
        Assert.Contains(services, d => d.ServiceType == typeof(ILoadingEventDispatcher));
        Assert.Contains(services, d => d.ServiceType == typeof(ILoadingFailureTracker));
    }

    [Fact]
    public void LoadingAndDirectoryModules_RegisteredDirectly_CoexistWithoutConflict()
    {
        var services = new ServiceCollection();

        // Register both modules directly
        LoadingRegistrationServices.Register(services);
        services.AddNuplaneDirectorySource(o =>
        {
            o.FeedName = "test-feed";
            o.DirectoryPath = Path.GetTempPath();
            o.TriggerReconciliationOnChange = false;
        });

        // Both modules' services must be present
        Assert.Contains(services, d => d.ServiceType == typeof(IPackageLoader));
        Assert.Contains(services, d => d.ServiceType == typeof(IDesiredPackageSource));
    }

    [Fact]
    public void DirectoryModuleBuilderExtension_RegistersFeedWithSourceTrust()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-integration-builder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(nuplane =>
            {
                nuplane.AddDirectoryFeed("builder-feed", root, feed =>
                {
                    feed.Include("Test.*");
                });
            });

            using var provider = services.BuildServiceProvider();
            var trust = provider.GetRequiredService<IOptions<SourceTrustOptions>>().Value;

            Assert.Contains("builder-feed", trust.AllowedSourceNames);
            Assert.Contains("Test.*", trust.AllowedPackageIds);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DirectoryAndLoadingModules_ThroughBuilder_CoexistWithoutConflict()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-integration-coexist", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(nuplane =>
            {
                nuplane.AddDirectoryFeed("dir-feed", root, feed =>
                {
                    feed.IncludeAll();
                });
                nuplane.AutoloadPackages();
            });

            // Both modules' types must be registered
            Assert.Contains(services, d => d.ServiceType == typeof(IDesiredPackageSource));
            Assert.Contains(services, d => d.ServiceType == typeof(IPackageLoader));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
