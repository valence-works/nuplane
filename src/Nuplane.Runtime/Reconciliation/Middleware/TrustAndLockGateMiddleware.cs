using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Observability;
using Nuplane.Store.State;
using Nuplane.Runtime.Reconciliation.FeedPolicy;

namespace Nuplane.Runtime.Reconciliation.Middleware;

internal sealed class TrustAndLockGateMiddleware(
    FeedResolutionOptions feedResolutionOptions,
    FeedTrustPolicyOptions feedTrustPolicyOptions,
    IFeedTrustPolicyEvaluator feedTrustPolicyEvaluator,
    ILockFileCoordinator lockFileCoordinator,
    IReconciliationRetryPolicy retryPolicy,
    IFailureRecorder failureRecorder,
    IReconciliationLogger logger) : IReconciliationMiddleware
{
    public async Task InvokeAsync(ReconciliationCycleContext context, Func<Task> next)
    {
        var resolutionResult = context.ResolutionResult!;

        var requestByPackageId = context.AllowlistedRequests
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var trustFailures = 0;
        var lockFailures = 0;
        var trustAndLockPassed = new List<ResolvedPackage>();
        var combinedFailures = new HashSet<string>(resolutionResult.FailedPackageIds, StringComparer.OrdinalIgnoreCase);

        foreach (var resolved in resolutionResult.ResolvedPackages)
        {
            var request = requestByPackageId.TryGetValue(resolved.Id, out var matchedRequest)
                ? matchedRequest
                : new(resolved.Id, resolved.Version, resolved.FeedName, PackageUpdatePolicy.Exact, resolved.SourceName);

            var feed = feedResolutionOptions.Feeds.FirstOrDefault(x =>
                string.Equals(x.Name, resolved.FeedName, StringComparison.OrdinalIgnoreCase))
                ?? new FeedDefinition(resolved.FeedName, new("https://unknown.invalid"), FeedTrustLevel.Trusted);

            var trustOutcome = feedTrustPolicyEvaluator.Evaluate(
                request,
                feed,
                feedTrustPolicyOptions,
                validatorPassed: true);

            logger.LogTrustPolicyOutcome(context.CorrelationId, resolved.Id, trustOutcome);

            if (!trustOutcome.Allowed)
            {
                trustFailures++;
                combinedFailures.Add(resolved.Id);
                await failureRecorder.RecordAsync(resolved.Id, "trust", trustOutcome.ReasonCode, context.CorrelationId, context.CancellationToken);
                continue;
            }

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
        context.TrustFailureCount = trustFailures;
        context.LockFailureCount = lockFailures;

        // Update resolution result with trust/lock filtered packages
        context.ResolutionResult = new(
            trustAndLockPassed,
            combinedFailures.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            resolutionResult.FeedDecisions);

        await next();
    }
}

