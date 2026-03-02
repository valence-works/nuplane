using System.Threading;
using Nuplane.Abstractions;
using Nuplane.NuGet.Resolution;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Sources;
using Nuplane.Store.Activation;
using Nuplane.Store.State;
using Nuplane.Store.Transactions;

namespace Nuplane.Runtime.Reconciliation;

public sealed record ReconciliationRunResult(
    bool Skipped,
    PackageChangeSet ChangeSet,
    IReadOnlyList<string> FailedPackages,
    bool IsDegraded);

public sealed class ReconciliationService
{
    private static readonly PackageChangeSet EmptyChangeSet = new([], [], [], string.Empty, DateTimeOffset.UtcNow);

    private readonly IReadOnlyList<IDesiredPackageSource> sources;
    private readonly SourceTrustOptions sourceTrustOptions;
    private readonly DesiredStateAggregator desiredStateAggregator;
    private readonly DesiredActualDiffEngine desiredActualDiffEngine;
    private readonly StoreRegistry storeRegistry;
    private readonly ReconciliationOptions reconciliationOptions;
    private readonly DesiredSourceSnapshotCache snapshotCache;
    private readonly ReconciliationRetryPolicy retryPolicy;
    private readonly AllowlistGate allowlistGate;
    private readonly PackageApplyExecutor applyExecutor;
    private readonly PackageChangeEventPublisher changeEventPublisher;
    private readonly ObserverNotifier observerNotifier;
    private readonly ReconciliationHealthEvaluator healthEvaluator;
    private readonly ReconciliationLogger logger;
    private readonly ReconciliationMetrics metrics;
    private readonly SemaphoreSlim cycleLock = new(1, 1);
    private int inFlight;

    public ReconciliationService(
        IEnumerable<IDesiredPackageSource> sources,
        SourceTrustOptions sourceTrustOptions,
        DesiredStateAggregator desiredStateAggregator,
        DesiredActualDiffEngine desiredActualDiffEngine,
        INuGetPackageResolver packageResolver,
        StoreRegistry storeRegistry,
        ReconciliationOptions reconciliationOptions)
        : this(
            sources,
            sourceTrustOptions,
            desiredStateAggregator,
            desiredActualDiffEngine,
            packageResolver,
            storeRegistry,
            reconciliationOptions,
            new PackageChangeEventPublisher(Array.Empty<INuplaneObserver>()),
            new ObserverNotifier(Array.Empty<INuplaneObserver>()),
            new ReconciliationHealthEvaluator(),
            new ReconciliationLogger(),
            new ReconciliationMetrics(new ReconciliationTelemetry()))
    {
    }

    public ReconciliationService(
        IEnumerable<IDesiredPackageSource> sources,
        SourceTrustOptions sourceTrustOptions,
        DesiredStateAggregator desiredStateAggregator,
        DesiredActualDiffEngine desiredActualDiffEngine,
        INuGetPackageResolver packageResolver,
        StoreRegistry storeRegistry,
        ReconciliationOptions reconciliationOptions,
        PackageChangeEventPublisher changeEventPublisher,
        ObserverNotifier observerNotifier,
        ReconciliationHealthEvaluator healthEvaluator,
        ReconciliationLogger? logger = null,
        ReconciliationMetrics? metrics = null)
    {
        this.sources = sources?.ToArray() ?? throw new ArgumentNullException(nameof(sources));
        this.sourceTrustOptions = sourceTrustOptions ?? throw new ArgumentNullException(nameof(sourceTrustOptions));
        this.desiredStateAggregator = desiredStateAggregator ?? throw new ArgumentNullException(nameof(desiredStateAggregator));
        this.desiredActualDiffEngine = desiredActualDiffEngine ?? throw new ArgumentNullException(nameof(desiredActualDiffEngine));
        this.storeRegistry = storeRegistry ?? throw new ArgumentNullException(nameof(storeRegistry));
        this.reconciliationOptions = reconciliationOptions ?? throw new ArgumentNullException(nameof(reconciliationOptions));
        this.changeEventPublisher = changeEventPublisher ?? throw new ArgumentNullException(nameof(changeEventPublisher));
        this.observerNotifier = observerNotifier ?? throw new ArgumentNullException(nameof(observerNotifier));
        this.healthEvaluator = healthEvaluator ?? throw new ArgumentNullException(nameof(healthEvaluator));
        this.logger = logger ?? new ReconciliationLogger();
        this.metrics = metrics ?? new ReconciliationMetrics(new ReconciliationTelemetry());

        var failureRecorder = new FailureRecorder(this.storeRegistry);
        var pointerSwitcher = new AtomicPointerSwitcher();
        var transactionCoordinator = new PackageTransactionCoordinator(pointerSwitcher, failureRecorder);

        retryPolicy = new ReconciliationRetryPolicy(this.reconciliationOptions);
        snapshotCache = new DesiredSourceSnapshotCache(this.storeRegistry);
        allowlistGate = new AllowlistGate();
        applyExecutor = new PackageApplyExecutor(
            packageResolver ?? throw new ArgumentNullException(nameof(packageResolver)),
            transactionCoordinator,
            retryPolicy,
            failureRecorder);
    }

    public async Task<ReconciliationRunResult> TriggerManualAsync(CancellationToken cancellationToken)
    {
        if (reconciliationOptions.EnableSingleFlight && Interlocked.CompareExchange(ref inFlight, 1, 0) != 0)
        {
            return new ReconciliationRunResult(true, EmptyChangeSet, Array.Empty<string>(), IsDegraded: false);
        }

        await cycleLock.WaitAsync(cancellationToken);
        try
        {
            var cycleStartedAt = DateTimeOffset.UtcNow;
            var correlationId = CorrelationContext.CreateNew();
            using var _ = CorrelationContext.BeginScope(correlationId);

            var readResult = await ReadDesiredRequestsWithFallbackAsync(cancellationToken);
            var desiredRequests = await desiredStateAggregator.AggregateAsync(
                [new StaticDesiredSource(readResult.Requests)],
                sourceTrustOptions,
                cancellationToken);
            var allowlistedRequests = allowlistGate.Enforce(desiredRequests, sourceTrustOptions);
            logger.LogCycleStarted(correlationId, allowlistedRequests.Count);
            var applyResult = await applyExecutor.ExecuteAsync(allowlistedRequests, correlationId, cancellationToken);

            var activeVersions = await storeRegistry.GetActiveVersionsAsync(cancellationToken);
            var changeSet = desiredActualDiffEngine.Compute(applyResult.AppliedPackages, activeVersions, correlationId, DateTimeOffset.UtcNow);

            if (changeSet.Added.Count + changeSet.Updated.Count + changeSet.Removed.Count > 0)
            {
                await changeEventPublisher.PublishChangingAsync(changeSet, cancellationToken);
            }

            foreach (var failedPackage in applyResult.FailedPackageIds)
            {
                await observerNotifier.NotifyPackageFailedAsync(
                    failedPackage,
                    new InvalidOperationException($"Package '{failedPackage}' failed to apply."),
                    correlationId,
                    cancellationToken);
            }

            var nextActive = desiredActualDiffEngine.BuildNextActiveVersions(applyResult.AppliedPackages);
            await storeRegistry.PersistActiveVersionsAsync(nextActive, correlationId, cancellationToken);

            if (changeSet.Added.Count + changeSet.Updated.Count + changeSet.Removed.Count > 0)
            {
                await changeEventPublisher.PublishChangedAsync(changeSet, cancellationToken);
            }

            var hadFailures = readResult.UsedFallback || applyResult.FailedPackageIds.Count > 0;
            var isDegraded = healthEvaluator.Evaluate(hadFailures, readResult.AllSourcesFresh);
            var cycleDuration = DateTimeOffset.UtcNow - cycleStartedAt;
            metrics.RecordCycle(changeSet, applyResult.FailedPackageIds.Count, cycleDuration, nextActive.Count);
            logger.LogCycleCompleted(correlationId, isDegraded, applyResult.FailedPackageIds.Count);

            return new ReconciliationRunResult(false, changeSet, applyResult.FailedPackageIds, isDegraded);
        }
        finally
        {
            cycleLock.Release();
            Interlocked.Exchange(ref inFlight, 0);
        }
    }

    private async Task<DesiredReadResult> ReadDesiredRequestsWithFallbackAsync(CancellationToken cancellationToken)
    {
        var requests = new List<PackageRequest>();
        var usedFallback = false;
        var freshReads = 0;

        foreach (var source in sources.OrderBy(x => x.GetType().FullName ?? x.GetType().Name, StringComparer.Ordinal))
        {
            var sourceName = source.GetType().FullName ?? source.GetType().Name;
            try
            {
                var fromSource = await retryPolicy.ExecuteAsync(ct => source.GetDesiredAsync(ct), cancellationToken);
                await snapshotCache.SaveAsync(sourceName, fromSource, cancellationToken);
                requests.AddRange(fromSource);
                freshReads++;
            }
            catch
            {
                usedFallback = true;
                if (snapshotCache.TryGetSnapshot(sourceName, out var cached))
                {
                    requests.AddRange(cached);
                }
            }
        }

        return new DesiredReadResult(
            requests,
            usedFallback,
            AllSourcesFresh: freshReads == sources.Count);
    }

    private sealed record DesiredReadResult(
        IReadOnlyList<PackageRequest> Requests,
        bool UsedFallback,
        bool AllSourcesFresh);

    private sealed class StaticDesiredSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) => Task.FromResult(requests);
    }
}