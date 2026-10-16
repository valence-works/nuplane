using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Events;
using Nuplane.Feeds.Configuration;
using Nuplane.Health;
using Nuplane.Hosting;
using Nuplane.Observability;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Sources;
using Nuplane.Store.Cleanup;
using Nuplane.Store.Coordination;
using Nuplane.Store.State;

namespace Nuplane.Tests.Shared;

internal static class ProtectedReconciliationServices
{
    internal static ReconciliationService Create(IServiceProvider services)
    {
        var loadingObserver = services.GetService<ILeasedPackageGraphLoadingObserver>();
        return new(
            services.GetServices<IDesiredPackageSource>(),
            services.GetRequiredService<IDesiredStateAggregator>(),
            services.GetRequiredService<IDesiredActualDiffEngine>(),
            services.GetRequiredService<IPackageResolver>(),
            services.GetRequiredService<IStoreRegistry>(),
            services.GetRequiredService<IOptions<ReconciliationOptions>>(),
            services.GetRequiredService<IObserverEventDispatcher>(),
            services.GetRequiredService<IReconciliationHealthEvaluator>(),
            services.GetRequiredService<IReconciliationLogger>(),
            services.GetRequiredService<ReconciliationMetrics>(),
            services.GetRequiredService<IOptions<FeedResolutionOptions>>(),
            services.GetRequiredService<ILockFileCoordinator>(),
            services.GetRequiredService<IOptions<CleanupPolicyOptions>>(),
            services.GetRequiredService<IReconciliationRetryPolicy>(),
            services.GetRequiredService<IDryRunPlanner>(),
            services.GetRequiredService<IPackageCleanupService>(),
            services.GetRequiredService<IFailureRecorder>(),
            services.GetRequiredService<ObservationDegradationTracker>(),
            services.GetService<ICycleFailureContributor>(),
            services.GetService<StartupRecoveryState>(),
            services.GetService<IStoreLock>(),
            services.GetServices<IDesiredStateContributor>(),
            services.GetRequiredService<IOptions<HostProvidedPackagesOptions>>(),
            services.GetRequiredService<IPackageStoreAdmission>(),
            loadingObserver is null ? null : services.GetService<IResolvedPackageGraphUseLeaseAcquisition>(),
            loadingObserver);
    }
}
