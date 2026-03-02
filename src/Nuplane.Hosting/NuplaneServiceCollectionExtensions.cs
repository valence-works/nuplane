using Microsoft.Extensions.DependencyInjection;
using Nuplane.NuGet.Resolution;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Store.State;

namespace Nuplane.Hosting;

public static class NuplaneServiceCollectionExtensions
{
    public static IServiceCollection AddNuplaneRuntime(
        this IServiceCollection services,
        Action<SourceTrustOptions>? configureSourceTrust = null,
        Action<ReconciliationOptions>? configureReconciliation = null,
        string? stateFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var sourceTrustOptions = new SourceTrustOptions();
        configureSourceTrust?.Invoke(sourceTrustOptions);

        var reconciliationOptions = new ReconciliationOptions();
        configureReconciliation?.Invoke(reconciliationOptions);

        services.AddSingleton(sourceTrustOptions);
        services.AddSingleton(reconciliationOptions);
        services.AddSingleton<DesiredStateAggregator>();
        services.AddSingleton<DesiredActualDiffEngine>();
        services.AddSingleton<ReconciliationTelemetry>();
        services.AddSingleton<ReconciliationMetrics>();
        services.AddSingleton<ReconciliationLogger>();
        services.AddSingleton<ReconciliationHealthEvaluator>();
        services.AddSingleton<PackageChangeEventPublisher>(sp =>
            new PackageChangeEventPublisher(
                sp.GetServices<Nuplane.Abstractions.INuplaneObserver>(),
                sp.GetRequiredService<ReconciliationLogger>()));
        services.AddSingleton<ObserverNotifier>(sp =>
            new ObserverNotifier(
                sp.GetServices<Nuplane.Abstractions.INuplaneObserver>(),
                sp.GetRequiredService<ReconciliationLogger>()));
        services.AddSingleton<INuGetPackageResolver, NuGetPackageResolver>();
        services.AddSingleton(new StoreRegistry(new StoreStateSerializer(), stateFilePath));
        services.AddSingleton<ReconciliationService>();

        return services;
    }
}