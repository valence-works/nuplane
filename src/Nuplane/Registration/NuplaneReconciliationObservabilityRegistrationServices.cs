using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Health;
using Nuplane.Observability;
using ReconciliationLogger = Nuplane.Observability.ReconciliationLogger;

namespace Nuplane.Registration;

internal static class NuplaneReconciliationObservabilityRegistrationServices
{
    internal static void RegisterTelemetryAndObservers(this IServiceCollection services)
    {
        services.AddSingleton<ReconciliationTelemetry>();
        services.AddSingleton<ReconciliationMetrics>();
        services.AddSingleton<ReconciliationLogger>();
        services.AddSingleton<IReconciliationLogger>(sp => sp.GetRequiredService<ReconciliationLogger>());
        services.AddSingleton<ReconciliationHealthEvaluator>();
        services.AddSingleton<IReconciliationHealthEvaluator>(sp => sp.GetRequiredService<ReconciliationHealthEvaluator>());
        services.AddSingleton<ObserverEventDispatcher>(sp =>
            new(
                sp.GetServices<INuplaneObserver>(),
                sp.GetRequiredService<IReconciliationLogger>()));
        services.AddSingleton<IObserverEventDispatcher>(sp => sp.GetRequiredService<ObserverEventDispatcher>());
    }
}
