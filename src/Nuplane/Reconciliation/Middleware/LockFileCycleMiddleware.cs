using Nuplane.Reconciliation.LockFile;

namespace Nuplane.Reconciliation.Middleware;

internal sealed class LockFileCycleMiddleware(ILockFileCycleCoordinator lockFileCoordinator) : IReconciliationMiddleware
{
    public async Task InvokeAsync(ReconciliationCycleContext context, Func<Task> next)
    {
        context.LockFileSnapshot = await lockFileCoordinator.CaptureAsync(context.CancellationToken);

        await next();

        if (context.LockFileSnapshot.Mode != LockFileMode.Generate ||
            context.Result is not { Skipped: false, IsDegraded: false, FailedPackages.Count: 0 } ||
            context.ResolutionResult is not { FailedPackageIds.Count: 0 } resolutionResult)
        {
            return;
        }

        await lockFileCoordinator.GenerateAsync(
            context.LockFileSnapshot,
            resolutionResult.ResolvedPackages,
            context.CycleStartedAt.ToUniversalTime(),
            context.CancellationToken);
    }
}
