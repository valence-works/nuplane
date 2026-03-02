using Microsoft.Extensions.DependencyInjection;
using Nuplane.NuGet.Resolution;
using Nuplane.Runtime.Configuration;
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
        services.AddSingleton<INuGetPackageResolver, NuGetPackageResolver>();
        services.AddSingleton(new StoreRegistry(new StoreStateSerializer(), stateFilePath));
        services.AddSingleton<ReconciliationService>();

        return services;
    }
}