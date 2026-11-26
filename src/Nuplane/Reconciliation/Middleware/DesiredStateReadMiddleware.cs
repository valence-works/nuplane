using Nuplane.Abstractions;
using Nuplane.Observability;
using Nuplane.Sources;
using Nuplane.Store.State;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Reconciliation.Middleware;

internal sealed class DesiredStateReadMiddleware(
    IReadOnlyList<IDesiredPackageSource> sources,
    IDesiredStateAggregator desiredStateAggregator,
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
        
        logger.LogCycleStarted(context.CorrelationId, aggregateResult.Requests.Count);

        await next();
    }

    private async Task<(DesiredReadResult Result, int SourceOutageCount)> ReadDesiredRequestsWithFallbackAsync(string correlationId, CancellationToken cancellationToken)
    {
        var requests = new List<PackageRequest>();
        var usedFallback = false;
        var freshReads = 0;
        var sourceOutageCount = 0;

        var orderedSources = DesiredPackageSourceAccess.Describe(sources);

        foreach (var entry in orderedSources)
        {
            var source = entry.Source;
            var sourceName = entry.DiagnosticName;
            try
            {
                var fromSource = await retryPolicy.ExecuteAsync(ct => source.GetDesiredAsync(ct), cancellationToken);
                if (entry.SnapshotKey is { } snapshotKey)
                {
                    await snapshotCache.SaveAsync(snapshotKey, fromSource, cancellationToken);
                }

                requests.AddRange(fromSource);
                freshReads++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (PackageStoreAdmissionException)
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

                var fallback = await snapshotCache.LoadForSourceAsync(entry, cancellationToken);
                if (fallback is not null)
                {
                    requests.AddRange(fallback);
                }
            }
        }

        return (new(
            requests,
            usedFallback,
            AllSourcesFresh: freshReads == orderedSources.Count), sourceOutageCount);
    }
}
