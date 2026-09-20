using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Observability;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Convergence;
using Nuplane.Sources;

namespace Nuplane.Registration;

internal static class NuplaneDesiredStatePlanningRegistrationServices
{
    internal static void RegisterDesiredStateAggregationAndDryRunPlanning(this IServiceCollection services)
    {
        services.AddSingleton<DesiredManifestReader>();
        services.AddSingleton(sp => new DesiredManifestPackageSource(
            sp.GetRequiredService<DesiredManifestReader>(),
            sp.GetRequiredService<IOptions<ConvergenceOptions>>().Value,
            sp.GetService<ReconciliationMetrics>()));
        services.AddSingleton<IDesiredPackageSource>(sp => sp.GetRequiredService<DesiredManifestPackageSource>());
        services.AddSingleton<DesiredStateAggregator>();
        services.AddSingleton<IDesiredStateAggregator>(sp => sp.GetRequiredService<DesiredStateAggregator>());
        services.AddSingleton<DesiredActualDiffEngine>();
        services.AddSingleton<IDesiredActualDiffEngine>(sp => sp.GetRequiredService<DesiredActualDiffEngine>());
        services.AddSingleton<FeedRuleResultSelector>();
        services.AddSingleton<DryRunPlanner>();
        services.AddSingleton<IDryRunPlanner>(sp => sp.GetRequiredService<DryRunPlanner>());
    }
}

