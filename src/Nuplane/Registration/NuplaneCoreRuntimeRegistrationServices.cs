using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Nuplane.Hosting;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Registration;

internal static class NuplaneCoreRuntimeRegistrationServices
{
    internal static void RegisterCoreServices(IServiceCollection services)
    {
        NuplaneDesiredStatePlanningRegistrationServices.RegisterDesiredStateAggregationAndDryRunPlanning(services);
        NuplaneFeedVersioningRegistrationServices.RegisterPolicyAndVersioning(services);
        NuplaneStorePersistenceRegistrationServices.RegisterLockingAndCleanup(services);
        NuplaneReconciliationObservabilityRegistrationServices.RegisterTelemetryAndObservers(services);
        NuplaneFeedVersioningRegistrationServices.RegisterPackageResolution(services);
        NuplaneStorePersistenceRegistrationServices.RegisterStorePersistence(services);
        NuplaneReconciliationObservabilityRegistrationServices.RegisterRuntime(services);
    }

    internal static void EnsureTriggerIngressServices(IServiceCollection services)
    {
        services.TryAddSingleton<ReconciliationTriggerQueue>();
        services.TryAddSingleton<IReconciliationTriggerIngress>(sp => sp.GetRequiredService<ReconciliationTriggerQueue>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ReconciliationTriggerDispatcherHostedService>());
    }
}
