using Nuplane.Abstractions;
using Nuplane.Observability;
using Nuplane.Store.State;

namespace Nuplane.Reconciliation.Middleware;

internal sealed class TrustAndLockGateMiddleware(
    ILockFileCoordinator lockFileCoordinator,
    IReconciliationRetryPolicy retryPolicy,
    IFailureRecorder failureRecorder,
    IReconciliationLogger logger) : IReconciliationMiddleware
{
    public async Task InvokeAsync(ReconciliationCycleContext context, Func<Task> next)
    {
        var resolutionResult = context.ResolutionResult!;
        var lockFailures = 0;
        var trustAndLockPassed = new List<ResolvedPackage>();
        var combinedFailures = new HashSet<string>(resolutionResult.FailedPackageIds, StringComparer.OrdinalIgnoreCase);

        foreach (var resolved in resolutionResult.ResolvedPackages)
        {
            var lockOutcome = await retryPolicy.ExecuteAsync(
                ct => lockFileCoordinator.EvaluateAsync(resolved, ct),
                context.CancellationToken);

            logger.LogLockOutcome(context.CorrelationId, resolved.Id, lockOutcome);

            if (!lockOutcome.Allowed || lockOutcome.EffectivePackage is null)
            {
                lockFailures++;
                combinedFailures.Add(resolved.Id);
                await failureRecorder.RecordAsync(resolved.Id, "lock", lockOutcome.ReasonCode, context.CorrelationId, context.CancellationToken);
                continue;
            }

            trustAndLockPassed.Add(lockOutcome.EffectivePackage);
        }

        context.TrustAndLockPassed = trustAndLockPassed;
        context.LockFailureCount = lockFailures;

        // Update resolution result with trust/lock filtered packages
        context.ResolutionResult = new(
            trustAndLockPassed,
            combinedFailures.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            resolutionResult.FeedDecisions,
            resolutionResult.ResolvedGraphs);

        await next();
    }
}
