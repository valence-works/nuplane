using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Events;
using Nuplane.Reconciliation.PackageFiles;
using Nuplane.Store.Cleanup;
using Nuplane.Store.Coordination;
using Nuplane.Store.State;
using Nuplane.Sources;

namespace Nuplane.Reconciliation;

internal static class CoordinatedReconciliationAdapters
{
    internal static void ValidateParticipants(
        IReadOnlyList<IDesiredPackageSource> sources,
        IPackageResolver packageResolver,
        IReadOnlyList<IDesiredStateContributor> contributors,
        IStoreRegistry storeRegistry,
        IFailureRecorder failureRecorder,
        IObserverEventDispatcher observerEventDispatcher,
        IPackageCleanupService packageCleanupService,
        ICycleFailureContributor? cycleFailureContributor,
        IDesiredActualDiffEngine desiredActualDiffEngine,
        IDryRunPlanner dryRunPlanner,
        ILockFileCoordinator lockFileCoordinator,
        IReconciliationRetryPolicy retryPolicy,
        ILeasedPackageGraphLoadingObserver? leasedPackageGraphLoadingObserver,
        IResolvedPackageGraphUseLeaseAcquisition? graphUseLeaseAcquisition)
    {
        if (storeRegistry is not ICoordinatedStoreRegistry)
            Refuse("The configured store registry has no coordinated member-state operations.");
        if (failureRecorder is not IScopedFailureRecorder)
            Refuse("The configured failure recorder has no scoped state-publication contract.");
        if (packageResolver is not IScopedPackageResolver)
            Refuse("The configured package resolver has no scoped package-store contract.");
        ValidateDesiredPackageSources(sources);
        if (contributors.Any(static contributor => contributor is not IScopedDesiredStateContributor and not IPackagePathIndependentDesiredStateContributor))
            Refuse("Every enrolled desired-state contributor must declare scoped access or package-path independence.");
        var scopedDispatcher = observerEventDispatcher as IScopedObserverEventDispatcher
            ?? throw new PackageStoreAdmissionException(
                PackageStoreAdmissionReason.UnsupportedParticipant,
                "The configured observer dispatcher has no scoped callback contract.");
        scopedDispatcher.ValidateCoordinatedParticipants();
        if (packageCleanupService is not IPackagePathIndependentPackageCleanupService)
            Refuse("The configured cleanup service has no package-path-independent policy contract.");
        if (cycleFailureContributor is not null
            and not IPackagePathIndependentCycleFailureContributor)
            Refuse("The configured cycle-failure contributor has no package-path-independent contract.");
        if (leasedPackageGraphLoadingObserver is not null
            and not ILeaseBoundPackageGraphLoadingObserver)
            Refuse("The configured deferred loader has no complete graph-lease consumption contract.");
        if (leasedPackageGraphLoadingObserver is not null && graphUseLeaseAcquisition is null)
            Refuse("Deferred package loading is configured without native graph-use publication.");
        if (!IsPathIndependentDiffEngine(desiredActualDiffEngine))
            Refuse("The configured desired-actual diff engine has no package-path-independent contract.");
        if (dryRunPlanner is not IPackagePathIndependentDryRunPlanner &&
            (dryRunPlanner is not DryRunPlanner builtInDryRunPlanner ||
             !IsPathIndependentDiffEngine(builtInDryRunPlanner.DiffEngine)))
            Refuse("The configured dry-run planner has no verified package-path-independent contract.");
        if (lockFileCoordinator is not IPackagePathIndependentLockFileCoordinator)
            Refuse("The configured lock-file coordinator has no package-path-independent contract.");
        if (retryPolicy is not IPackageStoreRefusalPreservingRetryPolicy)
            Refuse("The configured retry policy does not preserve package-store admission refusals.");
    }

    internal static void ValidateDesiredPackageSources(IReadOnlyList<IDesiredPackageSource> sources)
        => DesiredPackageSourceAccess.Validate(sources);

    internal static IReadOnlyList<IDesiredPackageSource> BindSources(
        IReadOnlyList<IDesiredPackageSource> sources,
        PackageStoreOperationOwner owner) => sources
        .Select(source => (IDesiredPackageSource)new OwnedDesiredPackageSource(source, owner))
        .ToArray();

    internal static IReadOnlyList<IDesiredStateContributor> BindContributors(
        IReadOnlyList<IDesiredStateContributor> contributors,
        PackageStoreOperationOwner owner) => contributors
        .Select(contributor => (IDesiredStateContributor)new OwnedDesiredStateContributor(contributor, owner))
        .ToArray();

    internal static IStoreRegistry BindStoreRegistry(IStoreRegistry registry, PackageStoreOperationOwner owner)
        => new OwnedStoreRegistry(registry, owner);

    internal static IFailureRecorder BindFailureRecorder(IFailureRecorder recorder, PackageStoreOperationOwner owner)
        => new OwnedFailureRecorder(recorder, owner);

    internal static IPackageResolver BindPackageResolver(IPackageResolver resolver, PackageStoreOperationOwner owner)
        => new OwnedPackageResolver(resolver, owner);

    internal static IObserverEventDispatcher BindObserverEventDispatcher(
        IObserverEventDispatcher dispatcher,
        PackageStoreOperationOwner owner)
        => new OwnedObserverEventDispatcher(dispatcher, owner);

    private static void Refuse(string message)
        => throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant, message);

    private static bool IsPathIndependentDiffEngine(IDesiredActualDiffEngine engine)
        => engine is IPackagePathIndependentDesiredActualDiffEngine;

    private sealed class OwnedDesiredPackageSource(IDesiredPackageSource source, PackageStoreOperationOwner owner)
        : IDesiredPackageSource
    {
        private readonly IDesiredPackageSource _source = source ?? throw new ArgumentNullException(nameof(source));
        private readonly PackageStoreOperationOwner _owner = owner ?? throw new ArgumentNullException(nameof(owner));

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
            => DesiredPackageSourceAccess.ReadAsync(_source, _owner, ct);
    }

    private sealed class OwnedPackageResolver(IPackageResolver resolver, PackageStoreOperationOwner owner)
        : IPackageResolver, IPackageGraphFileReader
    {
        private readonly IPackageResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        private readonly PackageStoreOperationOwner _owner = owner ?? throw new ArgumentNullException(nameof(owner));

        public async Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
        {
            if (_resolver is not IScopedPackageResolver scoped)
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                    "The package resolver has no scoped package-store contract.");
            using var borrow = _owner.Borrow();
            return await scoped.ResolveAsync(request, borrow, cancellationToken).ConfigureAwait(false);
        }

        public InstalledPackageGraphFiles ReadInstallFiles(ResolvedPackage package)
        {
            using var borrow = _owner.Borrow();
            return new NativePackageGraphFileReader(borrow).ReadInstallFiles(package);
        }
    }

    private sealed class OwnedDesiredStateContributor(IDesiredStateContributor contributor, PackageStoreOperationOwner owner)
        : IDesiredStateContributor
    {
        private readonly IDesiredStateContributor _contributor = contributor ?? throw new ArgumentNullException(nameof(contributor));
        private readonly PackageStoreOperationOwner _owner = owner ?? throw new ArgumentNullException(nameof(owner));

        public Task<DesiredStateContribution> ContributeAsync(DesiredStateContributionContext context, CancellationToken ct)
        {
            if (_contributor is IScopedDesiredStateContributor scoped)
                return ContributeScopedAsync(scoped, context, ct);
            if (_contributor is not IPackagePathIndependentDesiredStateContributor)
                return Task.FromException<DesiredStateContribution>(new PackageStoreAdmissionException(
                    PackageStoreAdmissionReason.UnsupportedParticipant,
                    "The desired-state contributor has no scoped package-store contract."));
            return _contributor.ContributeAsync(context, ct);
        }

        private async Task<DesiredStateContribution> ContributeScopedAsync(
            IScopedDesiredStateContributor contributor,
            DesiredStateContributionContext context,
            CancellationToken ct)
        {
            using var borrow = _owner.Borrow();
            return await contributor.ContributeAsync(context, borrow, ct).ConfigureAwait(false);
        }
    }

    private sealed class OwnedFailureRecorder(IFailureRecorder recorder, PackageStoreOperationOwner owner) : IFailureRecorder
    {
        private readonly IFailureRecorder _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        private readonly PackageStoreOperationOwner _owner = owner ?? throw new ArgumentNullException(nameof(owner));

        public Task RecordAsync(string packageId, string stage, string message, string correlationId,
            CancellationToken cancellationToken)
        {
            if (_recorder is not IScopedFailureRecorder scoped)
                return Task.FromException(new PackageStoreAdmissionException(
                    PackageStoreAdmissionReason.UnsupportedParticipant,
                    "The failure recorder has no scoped state-publication contract."));

            return RecordScopedAsync(scoped, packageId, stage, message, correlationId, cancellationToken);
        }

        private async Task RecordScopedAsync(IScopedFailureRecorder recorder, string packageId, string stage,
            string message, string correlationId, CancellationToken cancellationToken)
        {
            using var borrow = _owner.Borrow();
            await recorder.RecordAsync(packageId, stage, message, correlationId, borrow, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class OwnedObserverEventDispatcher(
        IObserverEventDispatcher dispatcher,
        PackageStoreOperationOwner owner) : IObserverEventDispatcher
    {
        private readonly IScopedObserverEventDispatcher _dispatcher = dispatcher as IScopedObserverEventDispatcher
            ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "The observer dispatcher has no scoped callback contract.");
        private readonly PackageStoreOperationOwner _owner = owner ?? throw new ArgumentNullException(nameof(owner));

        public Task PublishChangingAsync(PackageChangeSet changeSet, CancellationToken cancellationToken)
            => _dispatcher.PublishChangingAsync(changeSet, _owner, cancellationToken);

        public Task PublishChangedAsync(PackageChangeSet changeSet, CancellationToken cancellationToken)
            => _dispatcher.PublishChangedAsync(changeSet, _owner, cancellationToken);

        public Task NotifyPackageFailedAsync(string packageId, Exception exception, string correlationId,
            CancellationToken cancellationToken)
            => _dispatcher.NotifyPackageFailedAsync(packageId, exception, correlationId, _owner, cancellationToken);

        public Task PublishReconciledAsync(PackageChangeSet changeSet, IReadOnlyList<ResolvedPackage> appliedPackages,
            CancellationToken cancellationToken)
            => _dispatcher.PublishReconciledAsync(changeSet, appliedPackages, _owner, cancellationToken);
    }

    private sealed class OwnedStoreRegistry(IStoreRegistry registry, PackageStoreOperationOwner owner) : IStoreRegistry
    {
        private readonly ICoordinatedStoreRegistry _registry = registry as ICoordinatedStoreRegistry
            ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "The configured store registry has no coordinated member-state operations.");
        private readonly PackageStoreOperationOwner _owner = owner ?? throw new ArgumentNullException(nameof(owner));

        public async Task<IReadOnlyDictionary<string, string>> GetActiveVersionsAsync(CancellationToken cancellationToken)
            => (await GetStateAsync(cancellationToken).ConfigureAwait(false)).ActiveVersionById;

        public async Task<StoreStateRecord> GetStateAsync(CancellationToken cancellationToken)
        {
            using var borrow = _owner.Borrow();
            return await _registry.ReadCoordinatedStateAsync(borrow, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyDictionary<string, ActivePackageDescriptor>> GetActivePackageDescriptorsAsync(
            CancellationToken cancellationToken)
            => (await GetStateAsync(cancellationToken).ConfigureAwait(false)).ActivePackageDescriptorsByIdNormalized;

        public Task PersistActiveVersionsAsync(IReadOnlyDictionary<string, string> activeVersions,
            IReadOnlyDictionary<string, string> successfullyApplied, string correlationId,
            CancellationToken cancellationToken)
            => PersistActiveVersionsAsync(activeVersions, successfullyApplied, correlationId, cancellationToken,
                activePackageDescriptors: null, activeGraphs: null);

        public Task PersistActiveVersionsAsync(IReadOnlyDictionary<string, string> activeVersions,
            IReadOnlyDictionary<string, string> successfullyApplied, string correlationId,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, ActivePackageDescriptor>? activePackageDescriptors)
            => PersistActiveVersionsAsync(activeVersions, successfullyApplied, correlationId, cancellationToken,
                activePackageDescriptors, activeGraphs: null);

        public async Task PersistActiveVersionsAsync(IReadOnlyDictionary<string, string> activeVersions,
            IReadOnlyDictionary<string, string> successfullyApplied, string correlationId,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, ActivePackageDescriptor>? activePackageDescriptors,
            IReadOnlyDictionary<string, GraphActivationRecord>? activeGraphs)
        {
            ArgumentNullException.ThrowIfNull(activeVersions);
            ArgumentNullException.ThrowIfNull(successfullyApplied);
            ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

            using var borrow = _owner.Borrow();
            var prior = await _registry.ReadCoordinatedStateAsync(borrow, cancellationToken).ConfigureAwait(false);
            if (successfullyApplied.Count != 0 || !SameStringMap(activeVersions, prior.ActiveVersionById))
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                    "This enrolled runtime slice cannot publish a nonempty active package transition.", _owner.Root);

            // Descriptor and graph inputs are not accepted as proof of a new graph. The bounded empty
            // transition preserves the exact verified semantic snapshot already in the member state.
            var next = StoreRegistry.ProtectRegistryMutation(prior with { UpdatedAt = DateTimeOffset.UtcNow });
            await _registry.PersistCoordinatedActiveStateAsync(borrow, next, cancellationToken).ConfigureAwait(false);
        }

        public Task PersistFailureAsync(string packageId, string stage, string message, string correlationId,
            CancellationToken cancellationToken)
            => PersistFailureScopedAsync(packageId, stage, message, correlationId, cancellationToken);

        private async Task PersistFailureScopedAsync(string packageId, string stage, string message,
            string correlationId, CancellationToken cancellationToken)
        {
            using var borrow = _owner.Borrow();
            await _registry.PersistCoordinatedFailureAsync(borrow, packageId, stage, message, correlationId,
                cancellationToken).ConfigureAwait(false);
        }

        public Task PersistSourceSnapshotAsync(string sourceName, SourceSnapshotRef snapshot,
            CancellationToken cancellationToken)
            => PersistSourceScopedAsync(sourceName, snapshot, cancellationToken);

        private async Task PersistSourceScopedAsync(string sourceName, SourceSnapshotRef snapshot,
            CancellationToken cancellationToken)
        {
            using var borrow = _owner.Borrow();
            await _registry.PersistCoordinatedSourceSnapshotAsync(borrow, sourceName, snapshot, cancellationToken)
                .ConfigureAwait(false);
        }

        private static bool SameStringMap(IReadOnlyDictionary<string, string> left,
            IReadOnlyDictionary<string, string> right)
            => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) &&
                StringComparer.OrdinalIgnoreCase.Equals(pair.Value, value));
    }
}
