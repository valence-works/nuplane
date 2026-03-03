using Nuplane.Runtime.Observability;

namespace Nuplane.Runtime.Reconciliation.Middleware;

internal sealed class PackageResolutionMiddleware(
    IPackageApplyExecutor applyExecutor,
    IReconciliationLogger logger) : IReconciliationMiddleware
{
    public async Task InvokeAsync(ReconciliationCycleContext context, Func<Task> next)
    {
        var resolutionResult = await applyExecutor.ResolveAsync(
            context.AllowlistedRequests,
            context.CorrelationId,
            context.CancellationToken);

        foreach (var decision in resolutionResult.FeedDecisions)
        {
            logger.LogFeedDecision(decision);
        }

        context.ResolutionResult = resolutionResult;

        await next();
    }
}

