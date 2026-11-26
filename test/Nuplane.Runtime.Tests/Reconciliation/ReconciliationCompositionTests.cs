using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Nuplane.Abstractions;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Middleware;
using Nuplane.Sources;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class ReconciliationCompositionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreatePipeline_RegisteredServiceAndCompatibilityConstructor_UseCanonicalStageOrder(bool includesLockCycle)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(builder => builder.UseInMemoryStore());
        if (!includesLockCycle)
            services.AddSingleton(Substitute.For<ILockFileCoordinator>());
        using var provider = services.BuildServiceProvider();

        var registered = provider.GetRequiredService<ReconciliationService>();
        var composition = ReconciliationComposition.FromServices(
            provider, provider.GetRequiredService<IPackageResolver>(), admission: null,
            storeComposition: null, loadingObserver: null);
        var compatibility = new ReconciliationService(
            provider.GetServices<IDesiredPackageSource>(),
            composition.DesiredStateAggregator,
            composition.DiffEngine,
            composition.PackageResolver,
            composition.StoreRegistry,
            Options.Create(composition.Options),
            composition.ObserverDispatcher,
            composition.HealthEvaluator,
            composition.Logger,
            composition.Metrics,
            Options.Create(composition.FeedOptions),
            composition.LockFileCoordinator,
            Options.Create(composition.CleanupOptions),
            composition.RetryPolicy,
            composition.DryRunPlanner,
            composition.CleanupService,
            composition.FailureRecorder,
            composition.ObservationDegradationTracker);

        Type[] expected =
        [
            typeof(LockFileCycleMiddleware),
            typeof(DesiredStateReadMiddleware),
            typeof(PackageResolutionMiddleware),
            typeof(TrustAndLockGateMiddleware),
            typeof(DiffAndChangeEventMiddleware),
            typeof(TransactionExecutionMiddleware),
            typeof(CleanupMiddleware),
            typeof(HealthAndMetricsMiddleware)
        ];
        var registeredOrder = registered.PipelineStageTypes.ToArray();
        Assert.Equal(includesLockCycle ? expected : expected[1..], registeredOrder);
        Assert.Equal(registeredOrder, compatibility.PipelineStageTypes);
    }
}
