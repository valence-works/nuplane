using Nuplane.Loading;

namespace Nuplane.Runtime.Reconciliation;

internal sealed class NoOpPackageUnloadCoordinator : IPackageUnloadCoordinator
{
    public Task<(DeactivationAttempt deactivation, UnloadOutcomeRecord unload)> AttemptUnloadAsync(
        string packageId,
        PackageLoadContextHandle context,
        TimeSpan deactivationTimeout,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var deactivation = new DeactivationAttempt(
            packageId,
            now,
            (int)Math.Max(0, deactivationTimeout.TotalMilliseconds),
            Completed: false,
            TimedOut: false,
            OutcomeCode: "loading-services-not-registered",
            correlationId);

        var unload = new UnloadOutcomeRecord(
            packageId,
            AttemptNumber: 1,
            AttemptedAt: now,
            Outcome: UnloadOutcome.Failed,
            PendingReason: "Loading services are not registered.",
            RetryEligible: true,
            correlationId);

        return Task.FromResult((deactivation, unload));
    }
}