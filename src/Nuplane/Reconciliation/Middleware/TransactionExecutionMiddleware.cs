using Nuplane.Events;

namespace Nuplane.Reconciliation.Middleware;

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
            var reason = applyResult.FailureMessages is not null
                && applyResult.FailureMessages.TryGetValue(failedPackage, out var msg)
                ? $"Package '{failedPackage}' failed to apply: {msg}"
                : $"Package '{failedPackage}' failed to apply.";

            await observerEventDispatcher.NotifyPackageFailedAsync(
                failedPackage,
                new InvalidOperationException(reason),
                context.CorrelationId,
                context.CancellationToken);
        }

        // Merge applied packages into existing active state: preserve active versions for packages that failed
        var appliedVersions = desiredActualDiffEngine.BuildNextActiveVersions(applyResult.AppliedPackages);
        var mergedActive = new Dictionary<string, string>(context.ActiveVersions!, StringComparer.OrdinalIgnoreCase);

        foreach (var removedPackageId in context.ChangeSet?.Removed ?? [])
        {
            mergedActive.Remove(removedPackageId);
        }

        foreach (var (id, version) in appliedVersions)
        {
            mergedActive[id] = version;
        }

        context.MergedActive = mergedActive;

        await next();
    }
}

