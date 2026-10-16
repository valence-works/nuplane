using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Capabilities;
using Nuplane.Events;
using Nuplane.Feeds.Configuration;
using Nuplane.Health;
using Nuplane.Hosting;
using Nuplane.Observability;
using Nuplane.Operational;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Sources;
using Nuplane.Store.Coordination;
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
        services.RegisterSecretReferences();
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
        // Explicit factory, not TryAddSingleton<TService, TImplementation>: the constructor DI would
        // pick automatically resolves IStoreLock only by argument-type matching against whichever
        // constructor the container prefers, and a future constructor added to this type could
        // silently change which one that is. Naming the store-lock-aware constructor here means a
        // registration-order change can never drop the lock unnoticed.
        services.TryAddSingleton<ILastKnownGoodStartupRecoveryService>(sp =>
        {
            var composition = sp.GetRequiredService<PackageStoreCompositionSnapshot>();
            var storeRegistry = sp.GetRequiredService<IStoreRegistry>();
            var observerDispatcher = sp.GetRequiredService<IObserverEventDispatcher>();
            var admission = composition.IsParticipant
                ? RequireValidatedParticipantAdmission(sp, composition)
                : null;

            ILeasedPackageGraphLoadingObserver? leasedLoadingObserver = null;
            ILeasedPackageGraphRecoveryLoadingObserver? recoveryLoadingObserver = null;
            ICoordinatedStoreRegistry? coordinatedRegistry = null;
            PackageGraphUseLeaseAcquisition? graphUseAcquisition = null;
            ITrustedPackageStoreRootCatalog? trustedRoots = null;
            IScopedObserverEventDispatcher? scopedDispatcher = null;
            if (composition.IsParticipant)
            {
                leasedLoadingObserver = sp.GetService<ILeasedPackageGraphLoadingObserver>();
                recoveryLoadingObserver = sp.GetService<ILeasedPackageGraphRecoveryLoadingObserver>();
                coordinatedRegistry = storeRegistry as ICoordinatedStoreRegistry
                    ?? throw UnsupportedParticipantRecovery("The selected store registry has no coordinated state reader.");
                trustedRoots = sp.GetRequiredService<ITrustedPackageStoreRootCatalog>();
                scopedDispatcher = observerDispatcher as IScopedObserverEventDispatcher
                    ?? throw UnsupportedParticipantRecovery("The selected observer dispatcher has no coordinated scope.");

                graphUseAcquisition = sp.GetRequiredService<PackageGraphUseLeaseAcquisition>();
                if (!ReferenceEquals(graphUseAcquisition, sp.GetRequiredService<IResolvedPackageGraphUseLeaseAcquisition>()))
                    throw UnsupportedParticipantRecovery("The selected graph-use acquisition service differs from the built-in native publisher.");

                if (leasedLoadingObserver is not null)
                {
                    PackageStoreCompositionValidator.RequireSupportedParticipantExecutionRoute(
                        composition,
                        sp.GetRequiredService<IPackageResolver>(),
                        sp.GetRequiredService<CapabilityDesiredStateContributor>().MetadataReader);
                }

                if (recoveryLoadingObserver is not null &&
                    !ReferenceEquals(leasedLoadingObserver, recoveryLoadingObserver))
                {
                    throw UnsupportedParticipantRecovery(
                        "The selected recovery-loading observer differs from the validated leased-loading observer.");
                }
            }

            return new LastKnownGoodStartupRecoveryService(
                storeRegistry,
                observerDispatcher,
                sp.GetRequiredService<StartupRecoveryState>(),
                sp.GetServices<ICycleFailureContributor>(),
                sp.GetRequiredService<IOptions<ReconciliationOptions>>(),
                sp.GetService<IStoreLock>(),
                admission,
                coordinatedParticipantMode: composition.IsParticipant,
                coordinatedStoreRegistry: coordinatedRegistry,
                graphUseLeaseAcquisition: graphUseAcquisition,
                trustedRootCatalog: trustedRoots,
                scopedObserverEventDispatcher: scopedDispatcher,
                recoveryLoadingObserver: recoveryLoadingObserver,
                leasedLoadingObserverConfigured: leasedLoadingObserver is not null);
        });
        services.AddSingleton<ActivePackageCatalog>(sp =>
        {
            var composition = sp.GetRequiredService<PackageStoreCompositionSnapshot>();
            var storeRegistry = sp.GetRequiredService<IStoreRegistry>();
            var logger = sp.GetRequiredService<IReconciliationLogger>();
            var metrics = sp.GetRequiredService<ReconciliationMetrics>();
            var admission = composition.IsParticipant
                ? RequireValidatedParticipantAdmission(sp, composition)
                : null;
            return composition.IsParticipant
                ? new ActivePackageCatalog(storeRegistry, logger, metrics, admission)
                : new ActivePackageCatalog(storeRegistry, logger, metrics);
        });
        services.AddSingleton<IActivePackageCatalog>(sp => sp.GetRequiredService<ActivePackageCatalog>());
        services.TryAddSingleton<NuplaneStoreSnapshotAccess>(sp =>
        {
            var composition = sp.GetRequiredService<PackageStoreCompositionSnapshot>();
            if (!composition.IsParticipant)
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                    "An admitted active snapshot is available only from a coordinated participant composition.");

            var admission = RequireValidatedParticipantAdmission(sp, composition) as PackageStoreAdmission
                ?? throw UnsupportedParticipantRecovery("The selected snapshot route has no built-in native admission.");
            var registry = sp.GetRequiredService<IStoreRegistry>() as StoreRegistry
                ?? throw UnsupportedParticipantRecovery("The selected snapshot route has no built-in coordinated registry.");
            var serializer = sp.GetRequiredService<IStoreStateSerializer>() as StoreStateSerializer
                ?? throw UnsupportedParticipantRecovery("The selected snapshot route has no built-in protection serializer.");
            var graphUseAcquisition = sp.GetRequiredService<PackageGraphUseLeaseAcquisition>();
            var selectedGraphUseAcquisition = sp.GetRequiredService<IResolvedPackageGraphUseLeaseAcquisition>();
            var lifetimeObserver = sp.GetRequiredService<IPackageGraphUseLifetimeObserver>();
            return new NuplaneStoreSnapshotAccess(registry, serializer, admission, composition,
                graphUseAcquisition, lifetimeObserver, selectedGraphUseAcquisition);
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperationalStateContributor, PackageCatalogOperationalStateContributor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperationalStateContributor, StartupRecoveryOperationalStateContributor>());
        services.AddSingleton(sp =>
        {
            var composition = sp.GetRequiredService<PackageStoreCompositionSnapshot>();
            var loadingObserver = sp.GetService<ILeasedPackageGraphLoadingObserver>();
            var packageResolver = sp.GetRequiredService<IPackageResolver>();
            var capabilityContributor = sp.GetRequiredService<CapabilityDesiredStateContributor>();
            var admission = composition.IsParticipant
                ? RequireValidatedParticipantAdmission(sp, composition)
                : null;
            if (composition.IsParticipant)
            {
                PackageStoreCompositionValidator.RequireSupportedParticipantExecutionRoute(
                    composition,
                    packageResolver,
                    capabilityContributor.MetadataReader);
            }
            return new ReconciliationService(
            sp.GetServices<IDesiredPackageSource>(),
            sp.GetRequiredService<IDesiredStateAggregator>(),
            sp.GetRequiredService<IDesiredActualDiffEngine>(),
            packageResolver,
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
            sp.GetService<IStoreLock>(),
            sp.GetServices<IDesiredStateContributor>(),
            sp.GetRequiredService<IOptions<HostProvidedPackagesOptions>>(),
            admission,
            composition.IsParticipant && loadingObserver is not null
                ? sp.GetService<IResolvedPackageGraphUseLeaseAcquisition>()
                : null,
            composition.IsParticipant ? loadingObserver : null,
            composition);
        });
        services.AddSingleton<IReconciliationService>(sp => sp.GetRequiredService<ReconciliationService>());
    }

    private static IPackageStoreAdmission RequireValidatedParticipantAdmission(
        IServiceProvider services,
        PackageStoreCompositionSnapshot composition)
    {
        var admission = services.GetRequiredService<IPackageStoreAdmission>();
        PackageStoreCompositionValidator.RequireSupportedParticipantSelection(
            composition,
            services.GetRequiredService<IPhysicalStoreFileSystem>(),
            services.GetRequiredService<IStoreRegistry>(),
            services.GetRequiredService<IStoreStateSerializer>(),
            admission,
            services.GetRequiredService<ITrustedPackageStoreRootCatalog>(),
            services.GetRequiredService<ITrustedPackageStoreMemberDeclarationCatalog>(),
            services.GetRequiredService<IOptions<FeedResolutionOptions>>().Value);
        return admission;
    }

    private static PackageStoreAdmissionException UnsupportedParticipantRecovery(string message)
        => new(PackageStoreAdmissionReason.UnsupportedParticipant, message);

    private static void AddTriggerIngressServices(this IServiceCollection services)
    {
        services.AddSingleton<ReconciliationTriggerQueue>();
        services.AddSingleton<IReconciliationTriggerIngress>(sp => sp.GetRequiredService<ReconciliationTriggerQueue>());
        services.AddHostedService<ReconciliationTriggerDispatcherHostedService>();
        services.AddHostedService<NuplaneStartupHostedService>();
    }
}
