using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Capabilities;
using Nuplane.Metadata;
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

        // The capability contributor is the counterpart of a desired source: a source says what the
        // host wants before anything is resolved, a contributor says what the resolved packages
        // additionally require. Registered concrete-first so the ledger it writes — the only channel
        // that carries a refused unpinned contribution out of a cycle — can be resolved on its own.
        services.AddSingleton<NuplanePackageMetadataReader>();
        services.AddSingleton<IPackageMetadataReader>(sp => sp.GetRequiredService<NuplanePackageMetadataReader>());
        services.AddSingleton<CapabilityContributionLedger>();
        services.AddSingleton<CapabilityDesiredStateContributor>();
        services.AddSingleton<IDesiredStateContributor>(sp => sp.GetRequiredService<CapabilityDesiredStateContributor>());
        services.AddSingleton<DesiredStateAggregator>();
        services.AddSingleton<IDesiredStateAggregator>(sp => sp.GetRequiredService<DesiredStateAggregator>());
        services.AddSingleton<DesiredActualDiffEngine>();
        services.AddSingleton<IDesiredActualDiffEngine>(sp => sp.GetRequiredService<DesiredActualDiffEngine>());
        services.AddSingleton<FeedRuleResultSelector>();
        services.AddSingleton<DryRunPlanner>();
        services.AddSingleton<IDryRunPlanner>(sp => sp.GetRequiredService<DryRunPlanner>());
    }
}

