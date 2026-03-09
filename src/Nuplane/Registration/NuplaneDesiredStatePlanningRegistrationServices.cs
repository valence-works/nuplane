using Microsoft.Extensions.DependencyInjection;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Sources;

namespace Nuplane.Registration;

internal static class NuplaneDesiredStatePlanningRegistrationServices
{
    internal static void RegisterDesiredStateAggregationAndDryRunPlanning(IServiceCollection services)
    {
        services.AddSingleton<DesiredManifestReader>();
        services.AddSingleton<DesiredStateAggregator>();
        services.AddSingleton<IDesiredStateAggregator>(sp => sp.GetRequiredService<DesiredStateAggregator>());
        services.AddSingleton<DesiredActualDiffEngine>();
        services.AddSingleton<IDesiredActualDiffEngine>(sp => sp.GetRequiredService<DesiredActualDiffEngine>());
        services.AddSingleton<FeedRuleResultSelector>();
        services.AddSingleton<DryRunPlanner>();
        services.AddSingleton<IDryRunPlanner>(sp => sp.GetRequiredService<DryRunPlanner>());
    }
}

