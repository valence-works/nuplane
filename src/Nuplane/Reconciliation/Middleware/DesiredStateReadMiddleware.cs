using Nuplane.Abstractions;
using Nuplane.Observability;
using Nuplane.Sources;
using Nuplane.Store.State;
using Nuplane.Trust;
using Nuplane.Trust.Source;

namespace Nuplane.Reconciliation.Middleware;

internal sealed class DesiredStateReadMiddleware(
    IReadOnlyList<IDesiredPackageSource> sources,
    SourceTrustOptions sourceTrustOptions,
    IDesiredStateAggregator desiredStateAggregator,
    IAllowlistGate allowlistGate,
    IReconciliationRetryPolicy retryPolicy,
    DesiredSourceSnapshotCache snapshotCache,
    IFailureRecorder failureRecorder,
    IReconciliationLogger logger,
    ReconciliationMetrics metrics) : IReconciliationMiddleware
{
    public async Task InvokeAsync(ReconciliationCycleContext context, Func<Task> next)
    {
        var (readResult, sourceOutageCount) = await ReadDesiredRequestsWithFallbackAsync(context.CorrelationId, context.CancellationToken);
        context.ReadResult = readResult;
        context.SourceOutageCount = sourceOutageCount;

        var aggregateResult = await desiredStateAggregator.AggregateAsync(
            [new StaticDesiredSource(readResult.Requests)],
            sourceTrustOptions,
            context.CancellationToken);
        context.DesiredRequests = aggregateResult.Requests;

        foreach (var (sourceName, ex) in aggregateResult.SourceErrors)
        {
            await failureRecorder.RecordAsync(sourceName, "source-aggregate", ex.Message, context.CorrelationId, context.CancellationToken);
            logger.LogSourceOutage(context.CorrelationId, sourceName, ex.Message);
            metrics.RecordSourceOutage();
            context.SourceOutageCount++;
        }

        if (context.SourceOutageCount > 0)
        {
            logger.LogAggregationOutcome(context.CorrelationId, aggregateResult.Requests.Count, context.SourceOutageCount);
        }

        var allowlistedRequests = allowlistGate.Enforce(aggregateResult.Requests, sourceTrustOptions);
        context.AllowlistedRequests = allowlistedRequests;

        logger.LogCycleStarted(context.CorrelationId, allowlistedRequests.Count);

        await next();
    }

    private async Task<(DesiredReadResult Result, int SourceOutageCount)> ReadDesiredRequestsWithFallbackAsync(string correlationId, CancellationToken cancellationToken)
    {
        var requests = new List<PackageRequest>();
        var usedFallback = false;
        var freshReads = 0;
        var sourceOutageCount = 0;

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
                var fromSource = await retryPolicy.ExecuteAsync(ct => source.GetDesiredAsync(ct), cancellationToken);
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
                sourceOutageCount++;
                await failureRecorder.RecordAsync(sourceName, "source-read", ex.Message, correlationId, cancellationToken);
                logger.LogSourceOutage(correlationId, sourceName, ex.Message);
                metrics.RecordSourceOutage();

                var fallback = await snapshotCache.LoadSnapshotAsync(sourceName, cancellationToken);
                if (fallback is not null)
                {
                    requests.AddRange(fallback);
                }
            }
        }

        return (new(
            requests,
            usedFallback,
            AllSourcesFresh: freshReads == orderedSources.Length), sourceOutageCount);
    }
}

