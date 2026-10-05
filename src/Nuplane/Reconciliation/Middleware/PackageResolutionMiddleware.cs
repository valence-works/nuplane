using Nuplane.Observability;
using Nuplane.Reconciliation.LockFile;

namespace Nuplane.Reconciliation.Middleware;

internal sealed class PackageResolutionMiddleware(
    IPackageApplyExecutor applyExecutor,
    IReconciliationLogger logger,
    ILockFileCycleCoordinator? lockFileCoordinator = null) : IReconciliationMiddleware
{
    public async Task InvokeAsync(ReconciliationCycleContext context, Func<Task> next)
    {
        var resolutionResult = applyExecutor is PackageApplyExecutor concreteExecutor &&
            lockFileCoordinator is not null &&
            context.LockFileSnapshot is not null
                ? await concreteExecutor.ResolveAsync(
                    context.DesiredRequests,
                    context.CorrelationId,
                    lockFileCoordinator,
                    context.LockFileSnapshot,
                    context.CancellationToken)
                : await applyExecutor.ResolveAsync(
                    context.DesiredRequests,
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
