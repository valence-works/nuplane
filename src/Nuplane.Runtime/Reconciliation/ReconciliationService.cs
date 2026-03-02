using System.Threading;
using Nuplane.Abstractions;
using Nuplane.NuGet.Resolution;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Observability;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Reconciliation;

public sealed record ReconciliationRunResult(bool Skipped, PackageChangeSet ChangeSet);

public sealed class ReconciliationService
{
    private static readonly PackageChangeSet EmptyChangeSet = new([], [], [], string.Empty, DateTimeOffset.UtcNow);

    private readonly IReadOnlyList<IDesiredPackageSource> sources;
    private readonly SourceTrustOptions sourceTrustOptions;
    private readonly DesiredStateAggregator desiredStateAggregator;
    private readonly DesiredActualDiffEngine desiredActualDiffEngine;
    private readonly INuGetPackageResolver packageResolver;
    private readonly StoreRegistry storeRegistry;
    private readonly ReconciliationOptions reconciliationOptions;
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
    {
        this.sources = sources?.ToArray() ?? throw new ArgumentNullException(nameof(sources));
        this.sourceTrustOptions = sourceTrustOptions ?? throw new ArgumentNullException(nameof(sourceTrustOptions));
        this.desiredStateAggregator = desiredStateAggregator ?? throw new ArgumentNullException(nameof(desiredStateAggregator));
        this.desiredActualDiffEngine = desiredActualDiffEngine ?? throw new ArgumentNullException(nameof(desiredActualDiffEngine));
        this.packageResolver = packageResolver ?? throw new ArgumentNullException(nameof(packageResolver));
        this.storeRegistry = storeRegistry ?? throw new ArgumentNullException(nameof(storeRegistry));
        this.reconciliationOptions = reconciliationOptions ?? throw new ArgumentNullException(nameof(reconciliationOptions));
    }

    public async Task<ReconciliationRunResult> TriggerManualAsync(CancellationToken cancellationToken)
    {
        if (reconciliationOptions.EnableSingleFlight && Interlocked.CompareExchange(ref inFlight, 1, 0) != 0)
        {
            return new ReconciliationRunResult(true, EmptyChangeSet);
        }

        await cycleLock.WaitAsync(cancellationToken);
        try
        {
            var correlationId = CorrelationContext.CreateNew();
            using var _ = CorrelationContext.BeginScope(correlationId);

            var desiredRequests = await desiredStateAggregator.AggregateAsync(sources, sourceTrustOptions, cancellationToken);
            var resolvedDesired = new List<ResolvedPackage>(desiredRequests.Count);
            foreach (var request in desiredRequests)
            {
                resolvedDesired.Add(await packageResolver.ResolveAsync(request, cancellationToken));
            }

            var activeVersions = await storeRegistry.GetActiveVersionsAsync(cancellationToken);
            var changeSet = desiredActualDiffEngine.Compute(resolvedDesired, activeVersions, correlationId, DateTimeOffset.UtcNow);
            var nextActive = desiredActualDiffEngine.BuildNextActiveVersions(resolvedDesired);
            await storeRegistry.PersistActiveVersionsAsync(nextActive, correlationId, cancellationToken);

            return new ReconciliationRunResult(false, changeSet);
        }
        finally
        {
            cycleLock.Release();
            Interlocked.Exchange(ref inFlight, 0);
        }
    }
}