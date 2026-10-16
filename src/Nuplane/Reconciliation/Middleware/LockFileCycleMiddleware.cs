using Nuplane.Reconciliation.LockFile;

namespace Nuplane.Reconciliation.Middleware;

internal sealed class LockFileCycleMiddleware(ILockFileCycleCoordinator lockFileCoordinator) : IReconciliationMiddleware
{
    public async Task InvokeAsync(ReconciliationCycleContext context, Func<Task> next)
    {
        context.LockFileSnapshot = await lockFileCoordinator.CaptureAsync(context.CancellationToken);

        await next();

        await GenerateIfSuccessfulAsync(lockFileCoordinator, context).ConfigureAwait(false);
    }

    internal static Task GenerateIfSuccessfulAsync(
        ILockFileCycleCoordinator? lockFileCoordinator,
        ReconciliationCycleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (lockFileCoordinator is null ||
            context.LockFileSnapshot is not { Mode: LockFileMode.Generate } snapshot ||
            context.Result is not { Skipped: false, IsDegraded: false, FailedPackages.Count: 0 } ||
            context.ResolutionResult is not { FailedPackageIds.Count: 0 } resolutionResult)
        {
            return Task.CompletedTask;
        }

        return lockFileCoordinator.GenerateAsync(
            snapshot,
            resolutionResult.ResolvedPackages,
            context.CycleStartedAt.ToUniversalTime(),
            context.CancellationToken);
    }
}
