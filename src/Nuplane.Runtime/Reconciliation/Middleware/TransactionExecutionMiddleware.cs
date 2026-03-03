using Nuplane.Runtime.Events;

namespace Nuplane.Runtime.Reconciliation.Middleware;

internal sealed class TransactionExecutionMiddleware(
    IPackageApplyExecutor applyExecutor,
    IDesiredActualDiffEngine desiredActualDiffEngine,
    IObserverEventDispatcher observerEventDispatcher) : IReconciliationMiddleware
{
    public async Task InvokeAsync(ReconciliationCycleContext context, Func<Task> next)
    {
        // Phase 2: Execute transactions for resolved packages
        var applyResult = await applyExecutor.ExecuteTransactionsAsync(
            context.ResolutionResult!,
            context.CorrelationId,
            context.CancellationToken);
        context.ApplyResult = applyResult;

        foreach (var failedPackage in applyResult.FailedPackageIds)
        {
            await observerEventDispatcher.NotifyPackageFailedAsync(
                failedPackage,
                new InvalidOperationException($"Package '{failedPackage}' failed to apply."),
                context.CorrelationId,
                context.CancellationToken);
        }

        // Merge applied packages into existing active state: preserve active versions for packages that failed
        var appliedVersions = desiredActualDiffEngine.BuildNextActiveVersions(applyResult.AppliedPackages);
        var mergedActive = new Dictionary<string, string>(context.ActiveVersions!, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, version) in appliedVersions)
        {
            mergedActive[id] = version;
        }

        context.MergedActive = mergedActive;

        await next();
    }
}

