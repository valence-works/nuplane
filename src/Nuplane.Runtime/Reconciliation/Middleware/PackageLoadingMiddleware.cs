using Nuplane.Abstractions;
using Nuplane.Loading;
using Nuplane.Loading.Configuration;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Observability;

namespace Nuplane.Runtime.Reconciliation.Middleware;

internal sealed class PackageLoadingMiddleware(
    LoadingOptions loadingOptions,
    IPackageLoader packageLoader,
    IAllowlistGate allowlistGate,
    IPackageApplyExecutor applyExecutor,
    IObserverEventDispatcher observerEventDispatcher,
    IReconciliationLogger logger,
    ReconciliationMetrics metrics) : IReconciliationMiddleware
{
    public async Task InvokeAsync(ReconciliationCycleContext context, Func<Task> next)
    {
        if (loadingOptions.Enabled && context.TrustAndLockPassed.Count > 0)
        {
            // Validate install paths are within the trusted store root before loading
            if (!string.IsNullOrWhiteSpace(loadingOptions.ActiveStoreRoot))
            {
                foreach (var package in context.TrustAndLockPassed)
                {
                    allowlistGate.EnsureActiveStorePath(package.Id, package.InstallPath, loadingOptions.ActiveStoreRoot);
                }
            }

            var sharedPolicy = loadingOptions.SharedAssemblies
                .Select(x => new SharedAssemblyPolicyEntry(x.Name, x.PublicKeyToken, x.MajorVersion))
                .ToArray();

            foreach (var package in context.TrustAndLockPassed)
            {
                metrics.RecordLoadAttemptStarted();
            }

            var loadResult = await packageLoader.EnsureLoadedAsync(context.TrustAndLockPassed, sharedPolicy, context.CancellationToken);

            var failedLoadIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var package in context.TrustAndLockPassed)
            {
                if (loadResult.FailedByPackageId.TryGetValue(package.Id, out var reason))
                {
                    failedLoadIds.Add(package.Id);
                    metrics.RecordLoadFailed();
                    logger.LogLoadOutcome(context.CorrelationId, package.Id, succeeded: false, reason);
                    await applyExecutor.RecordLoadingFailureNonMutatingAsync(package.Id, context.CorrelationId, reason, context.CancellationToken);
                    await observerEventDispatcher.NotifyPackageFailedAsync(
                        package.Id,
                        new InvalidOperationException(reason),
                        context.CorrelationId,
                        context.CancellationToken);
                }
                else
                {
                    metrics.RecordLoadSucceeded();
                    logger.LogLoadOutcome(context.CorrelationId, package.Id, succeeded: true, reason: null);
                }
            }

            // Update trust-and-lock-passed to exclude load failures
            context.TrustAndLockPassed = context.TrustAndLockPassed
                .Where(x => !failedLoadIds.Contains(x.Id))
                .ToList();

            // Update combined failures in resolution result
            var combinedFailures = new HashSet<string>(context.ResolutionResult!.FailedPackageIds, StringComparer.OrdinalIgnoreCase);
            foreach (var id in failedLoadIds)
            {
                combinedFailures.Add(id);
            }

            context.ResolutionResult = new(
                context.TrustAndLockPassed,
                combinedFailures.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                context.ResolutionResult.FeedDecisions);
        }

        await next();
    }
}

