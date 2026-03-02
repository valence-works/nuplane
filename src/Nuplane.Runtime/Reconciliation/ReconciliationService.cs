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
    private readonly FailureRecorder failureRecorder;
    private readonly FeedResolutionOptions feedResolutionOptions;
    private readonly FeedTrustPolicyOptions feedTrustPolicyOptions;
    private readonly LockFileOptions lockFileOptions;
    private readonly CleanupPolicyOptions cleanupPolicyOptions;
    private readonly FeedTrustPolicyEvaluator feedTrustPolicyEvaluator;
    private readonly LockFileCoordinator lockFileCoordinator;
    private readonly DryRunPlanner dryRunPlanner;
    private readonly PackageCleanupService packageCleanupService;
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
                new ReconciliationMetrics(new ReconciliationTelemetry()),
                new FeedResolutionOptions(),
                new FeedTrustPolicyOptions(),
                new LockFileOptions())
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
        ReconciliationMetrics? metrics = null,
        FeedResolutionOptions? feedResolutionOptions = null,
        FeedTrustPolicyOptions? feedTrustPolicyOptions = null,
        LockFileOptions? lockFileOptions = null,
        CleanupPolicyOptions? cleanupPolicyOptions = null)
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
        this.feedResolutionOptions = feedResolutionOptions ?? new FeedResolutionOptions();
        this.feedTrustPolicyOptions = feedTrustPolicyOptions ?? new FeedTrustPolicyOptions();
        this.lockFileOptions = lockFileOptions ?? new LockFileOptions();
        this.cleanupPolicyOptions = cleanupPolicyOptions ?? new CleanupPolicyOptions();
        this.feedTrustPolicyEvaluator = new FeedTrustPolicyEvaluator();
        this.lockFileCoordinator = new LockFileCoordinator(new LockFileStore(this.lockFileOptions.Path), this.lockFileOptions);
        this.dryRunPlanner = new DryRunPlanner(this.desiredActualDiffEngine);
        this.packageCleanupService = new PackageCleanupService(new CleanupPolicyEvaluator());

        var failureRecorder = new FailureRecorder(this.storeRegistry);
        this.failureRecorder = failureRecorder;
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

            var readResult = await ReadDesiredRequestsWithFallbackAsync(correlationId, cancellationToken);
            var desiredRequests = await desiredStateAggregator.AggregateAsync(
                [new StaticDesiredSource(readResult.Requests)],
                sourceTrustOptions,
                cancellationToken);
            var allowlistedRequests = allowlistGate.Enforce(desiredRequests, sourceTrustOptions);
            logger.LogCycleStarted(correlationId, allowlistedRequests.Count);

            // Phase 1: Resolve packages to determine the desired target versions
            var resolutionResult = await applyExecutor.ResolveAsync(allowlistedRequests, correlationId, cancellationToken);
            foreach (var decision in resolutionResult.FeedDecisions)
            {
                logger.LogFeedDecision(decision);
            }

            var requestByPackageId = allowlistedRequests
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

            var trustFailures = 0;
            var lockFailures = 0;
            var trustAndLockPassed = new List<ResolvedPackage>();
            var combinedFailures = new HashSet<string>(resolutionResult.FailedPackageIds, StringComparer.OrdinalIgnoreCase);

            foreach (var resolved in resolutionResult.ResolvedPackages)
            {
                var request = requestByPackageId.TryGetValue(resolved.Id, out var matchedRequest)
                    ? matchedRequest
                    : new PackageRequest(resolved.Id, resolved.Version, resolved.FeedName, PackageUpdatePolicy.Exact, resolved.SourceName);

                var feed = feedResolutionOptions.Feeds.FirstOrDefault(x =>
                    string.Equals(x.Name, resolved.FeedName, StringComparison.OrdinalIgnoreCase))
                    ?? new FeedDefinition(resolved.FeedName, new Uri("https://unknown.invalid"), FeedTrustLevel.Untrusted);

                var trustOutcome = feedTrustPolicyEvaluator.Evaluate(
                    request,
                    feed,
                    feedTrustPolicyOptions,
                    validatorPassed: true);

                logger.LogTrustPolicyOutcome(correlationId, resolved.Id, trustOutcome);

                if (!trustOutcome.Allowed)
                {
                    trustFailures++;
                    combinedFailures.Add(resolved.Id);
                    await failureRecorder.RecordAsync(resolved.Id, "trust", trustOutcome.ReasonCode, correlationId, cancellationToken);
                    continue;
                }

                var lockOutcome = await retryPolicy.ExecuteForLockEvaluationAsync(
                    ct => lockFileCoordinator.EvaluateAsync(resolved, ct),
                    cancellationToken);

                logger.LogLockOutcome(correlationId, resolved.Id, lockOutcome);

                if (!lockOutcome.Allowed || lockOutcome.EffectivePackage is null)
                {
                    lockFailures++;
                    combinedFailures.Add(resolved.Id);
                    await failureRecorder.RecordAsync(resolved.Id, "lock", lockOutcome.ReasonCode, correlationId, cancellationToken);
                    continue;
                }

                trustAndLockPassed.Add(lockOutcome.EffectivePackage);
            }

            resolutionResult = new PackageResolutionResult(
                trustAndLockPassed,
                combinedFailures.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                resolutionResult.FeedDecisions);

            // Compute diff against pre-apply active state so Changing fires with accurate data
            var activeVersions = await storeRegistry.GetActiveVersionsAsync(cancellationToken);

            var dryRunPlan = await retryPolicy.ExecuteForDryRunAsync(
                ct => dryRunPlanner.BuildPlanAsync(
                    resolutionResult.ResolvedPackages,
                    activeVersions,
                    correlationId,
                    ct),
                cancellationToken);
            metrics.RecordDryRun(dryRunPlan);

            var changeSet = desiredActualDiffEngine.Compute(resolutionResult.ResolvedPackages, activeVersions, correlationId, DateTimeOffset.UtcNow);

            // Emit Changing before transactions begin (observer contract)
            if (changeSet.Added.Count + changeSet.Updated.Count + changeSet.Removed.Count > 0)
            {
                await changeEventPublisher.PublishChangingAsync(changeSet, cancellationToken);
            }

            // Phase 2: Execute transactions for resolved packages
            var applyResult = await applyExecutor.ExecuteTransactionsAsync(resolutionResult, correlationId, cancellationToken);

            foreach (var failedPackage in applyResult.FailedPackageIds)
            {
                await observerNotifier.NotifyPackageFailedAsync(
                    failedPackage,
                    new InvalidOperationException($"Package '{failedPackage}' failed to apply."),
                    correlationId,
                    cancellationToken);
            }

            // Merge applied packages into existing active state: preserve active versions for packages that failed
            var appliedVersions = desiredActualDiffEngine.BuildNextActiveVersions(applyResult.AppliedPackages);
            var mergedActive = new Dictionary<string, string>(activeVersions, StringComparer.OrdinalIgnoreCase);
            foreach (var (id, version) in appliedVersions)
            {
                mergedActive[id] = version;
            }

            // Only remove packages that are truly no longer desired (not in the request list at all)
            // Resolution/transaction failures should preserve the previous active version
            var requestedIds = new HashSet<string>(
                allowlistedRequests.Select(r => r.Id),
                StringComparer.OrdinalIgnoreCase);
            foreach (var activeId in activeVersions.Keys)
            {
                if (!requestedIds.Contains(activeId))
                {
                    mergedActive.Remove(activeId);
                }
            }

            await storeRegistry.PersistActiveVersionsAsync(mergedActive, appliedVersions, correlationId, cancellationToken);

            var storeState = await storeRegistry.GetStateAsync(cancellationToken);
            var cleanupInputs = mergedActive
                .Select(x => new PackageVersionEntry(
                    x.Key,
                    x.Value,
                    storeState.UpdatedAt,
                    IsLastKnownGood: storeState.LastKnownGoodById.TryGetValue(x.Key, out var lkgVersion) &&
                        string.Equals(lkgVersion, x.Value, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            var cleanupResults = await packageCleanupService.ExecuteAutomaticAsync(
                cleanupInputs,
                cleanupPolicyOptions,
                correlationId,
                triggerOnSuccessfulReconciliation: applyResult.FailedPackageIds.Count == 0,
                cancellationToken);
            metrics.RecordCleanup(cleanupResults);
            var cleanupFailures = cleanupResults.Count(x => x.Action == CleanupAction.Blocked);

            if (changeSet.Added.Count + changeSet.Updated.Count + changeSet.Removed.Count > 0)
            {
                await changeEventPublisher.PublishChangedAsync(changeSet, cancellationToken);
            }

            var hadFailures = readResult.UsedFallback || applyResult.FailedPackageIds.Count > 0;
            var isDegraded = healthEvaluator.Evaluate(hadFailures, readResult.AllSourcesFresh, trustFailures, lockFailures, cleanupFailures);
            var cycleDuration = DateTimeOffset.UtcNow - cycleStartedAt;
            metrics.RecordCycle(changeSet, applyResult.FailedPackageIds.Count, cycleDuration, mergedActive.Count);
            logger.LogCycleCompleted(correlationId, isDegraded, applyResult.FailedPackageIds.Count);

            return new ReconciliationRunResult(false, changeSet, applyResult.FailedPackageIds, isDegraded);
        }
        finally
        {
            cycleLock.Release();
            Interlocked.Exchange(ref inFlight, 0);
        }
    }

    private async Task<DesiredReadResult> ReadDesiredRequestsWithFallbackAsync(string correlationId, CancellationToken cancellationToken)
    {
        var requests = new List<PackageRequest>();
        var usedFallback = false;
        var freshReads = 0;

        var orderedSources = sources
            .Select(source => new
            {
                Source = source,
                SourceName = source.GetType().FullName ?? source.GetType().Name
            })
            .OrderBy(x => x.SourceName, StringComparer.Ordinal)
            .ToArray();

        foreach (var entry in orderedSources)
        {
            var source = entry.Source;
            var sourceName = entry.SourceName;
            try
            {
                var fromSource = await retryPolicy.ExecuteForFeedResolutionAsync(ct => source.GetDesiredAsync(ct), cancellationToken);
                await snapshotCache.SaveAsync(sourceName, fromSource, cancellationToken);
                requests.AddRange(fromSource);
                freshReads++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                usedFallback = true;
                await failureRecorder.RecordAsync(sourceName, "source-read", ex.Message, correlationId, cancellationToken);

                var fallback = await snapshotCache.LoadSnapshotAsync(sourceName, cancellationToken);
                if (fallback is not null)
                {
                    requests.AddRange(fallback);
                }
            }
        }

        return new DesiredReadResult(
            requests,
            usedFallback,
                AllSourcesFresh: freshReads == orderedSources.Length);
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