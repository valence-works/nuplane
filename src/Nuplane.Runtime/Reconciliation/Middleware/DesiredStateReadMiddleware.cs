using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Sources;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Reconciliation.Middleware;

internal sealed class DesiredStateReadMiddleware(
    IReadOnlyList<IDesiredPackageSource> sources,
    SourceTrustOptions sourceTrustOptions,
    IDesiredStateAggregator desiredStateAggregator,
    IAllowlistGate allowlistGate,
    IReconciliationRetryPolicy retryPolicy,
    DesiredSourceSnapshotCache snapshotCache,
    IFailureRecorder failureRecorder,
    IReconciliationLogger logger) : IReconciliationMiddleware
{
    public async Task InvokeAsync(ReconciliationCycleContext context, Func<Task> next)
    {
        var readResult = await ReadDesiredRequestsWithFallbackAsync(context.CorrelationId, context.CancellationToken);
        context.ReadResult = readResult;

        var desiredRequests = await desiredStateAggregator.AggregateAsync(
            [new StaticDesiredSource(readResult.Requests)],
            sourceTrustOptions,
            context.CancellationToken);
        context.DesiredRequests = desiredRequests;

        var allowlistedRequests = allowlistGate.Enforce(desiredRequests, sourceTrustOptions);
        context.AllowlistedRequests = allowlistedRequests;

        logger.LogCycleStarted(context.CorrelationId, allowlistedRequests.Count);

        await next();
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
                await failureRecorder.RecordAsync(sourceName, "source-read", ex.Message, correlationId, cancellationToken);

                var fallback = await snapshotCache.LoadSnapshotAsync(sourceName, cancellationToken);
                if (fallback is not null)
                {
                    requests.AddRange(fallback);
                }
            }
        }

        return new(
            requests,
            usedFallback,
            AllSourcesFresh: freshReads == orderedSources.Length);
    }
}

