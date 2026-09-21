using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Feeds.Configuration;
using Nuplane.Health;
using Nuplane.Hosting;
using Nuplane.Observability;
using Nuplane.Operational;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Sources;
using Nuplane.Store.Cleanup;
using Nuplane.Store.State;
using Polly;

namespace Nuplane.Registration;

/// <summary>
/// Provides extension methods for registering Nuplane Runtime services, including configuration validators and other runtime-specific dependencies.
/// </summary>
public static class NuplaneRuntimeRegistrationServices
{
    /// <summary>
    /// Registers Nuplane Runtime services, including the composite validator for feed resolution options that ensures feed credentials are valid according to the configured trust policies and source trust settings.
    /// </summary>
    public static IServiceCollection RegisterRuntime(this IServiceCollection services)
    {
        services.RegisterCoreServices();
        services.RegisterDesiredStateAggregationAndDryRunPlanning();
        services.RegisterPolicyAndVersioning();
        services.RegisterLockingAndCleanup();
        services.RegisterTelemetryAndObservers();
        services.RegisterPackageResolution();
        services.RegisterStorePersistence();
        services.AddTriggerIngressServices();
        return services;
    }

    private static void RegisterCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<FeedResolutionOptions>, FeedCredentialCompositeValidator>();
        services.AddSingleton<FailureRecorder>();
        services.AddSingleton<IFailureRecorder>(sp => sp.GetRequiredService<FailureRecorder>());
        services.AddResiliencePipeline(ReconciliationRetryPolicy.PipelineName, static (builder, context) =>
        {
            var options = context.ServiceProvider.GetRequiredService<IOptions<ReconciliationOptions>>().Value;
            if (options.MaxRetryAttempts == 0)
            {
                return;
            }

            builder.AddRetry(ReconciliationRetryPolicy.CreateRetryOptions(options));
        });
        services.AddSingleton<ReconciliationRetryPolicy>();
        services.AddSingleton<IReconciliationRetryPolicy>(sp => sp.GetRequiredService<ReconciliationRetryPolicy>());
        services.TryAddSingleton<ObservationDegradationTracker>();
        services.TryAddSingleton<StartupRecoveryState>();
        services.TryAddSingleton<ILastKnownGoodStartupRecoveryService, LastKnownGoodStartupRecoveryService>();
        services.AddSingleton<ActivePackageCatalog>();
        services.AddSingleton<IActivePackageCatalog>(sp => sp.GetRequiredService<ActivePackageCatalog>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperationalStateContributor, PackageCatalogOperationalStateContributor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperationalStateContributor, StartupRecoveryOperationalStateContributor>());
        services.AddSingleton(sp => new ReconciliationService(
            sp.GetServices<IDesiredPackageSource>(),
            sp.GetRequiredService<IDesiredStateAggregator>(),
            sp.GetRequiredService<IDesiredActualDiffEngine>(),
            sp.GetRequiredService<IPackageResolver>(),
            sp.GetRequiredService<IStoreRegistry>(),
            sp.GetRequiredService<IOptions<ReconciliationOptions>>(),
            sp.GetRequiredService<IObserverEventDispatcher>(),
            sp.GetRequiredService<IReconciliationHealthEvaluator>(),
            sp.GetRequiredService<IReconciliationLogger>(),
            sp.GetRequiredService<ReconciliationMetrics>(),
            sp.GetRequiredService<IOptions<FeedResolutionOptions>>(),
            sp.GetRequiredService<ILockFileCoordinator>(),
            sp.GetRequiredService<IOptions<CleanupPolicyOptions>>(),
            sp.GetRequiredService<IReconciliationRetryPolicy>(),
            sp.GetRequiredService<IDryRunPlanner>(),
            sp.GetRequiredService<IPackageCleanupService>(),
            sp.GetRequiredService<IFailureRecorder>(),
            sp.GetRequiredService<ObservationDegradationTracker>(),
            sp.GetService<ICycleFailureContributor>(),
            sp.GetService<StartupRecoveryState>(),
            sp.GetService<IStoreLock>()));
        services.AddSingleton<IReconciliationService>(sp => sp.GetRequiredService<ReconciliationService>());
    }

    private static void AddTriggerIngressServices(this IServiceCollection services)
    {
        services.AddSingleton<ReconciliationTriggerQueue>();
        services.AddSingleton<IReconciliationTriggerIngress>(sp => sp.GetRequiredService<ReconciliationTriggerQueue>());
        services.AddHostedService<ReconciliationTriggerDispatcherHostedService>();
        services.AddHostedService<NuplaneStartupHostedService>();
    }
}
