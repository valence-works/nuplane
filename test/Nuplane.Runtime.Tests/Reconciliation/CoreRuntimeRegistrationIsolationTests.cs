using Microsoft.Extensions.DependencyInjection;
using Nuplane.Loading;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;
using Nuplane.Sources;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class CoreRuntimeRegistrationIsolationTests
{
    [Fact]
    public void AddNuplane_RegistersDesiredStateAggregationAndDryRunPlanningServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(_ => { });

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IDesiredStateAggregator>());
        Assert.NotNull(provider.GetRequiredService<IDryRunPlanner>());
    }

    [Fact]
    public async Task AddNuplane_ResolvesAndRunsWithoutLoadingRegistration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var stateRoot = Path.Combine(Path.GetTempPath(), "nuplane-core-registration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateRoot);
        var stateFilePath = Path.Combine(stateRoot, "state.json");

        services.AddNuplane(nuplane =>
        {
            nuplane.WithStateFile(stateFilePath);
            // No AutoloadPackages() — verifies the runtime resolves without loading registration.
        });

        await using var provider = services.BuildServiceProvider();

        var runtime = provider.GetRequiredService<ReconciliationService>();
        var result = await runtime.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.FailedPackages);
    }

    [Fact]
    public void AddNuplane_DoesNotRegisterLoadingServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(_ => { });

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IPackageLoader));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IPackageUnloadCoordinator));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ILoadingEventDispatcher));
    }

    [Fact]
    public void AddNuplane_DoesNotRegisterLoadingOptionsValidator()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(_ => { });

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(LoadingOptionsValidator));
    }
}
