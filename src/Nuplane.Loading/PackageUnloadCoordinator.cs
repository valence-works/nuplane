using System.Collections.Concurrent;

namespace Nuplane.Loading;

public sealed class PackageUnloadCoordinator
{
    private readonly ConcurrentDictionary<string, int> attempts = new(StringComparer.OrdinalIgnoreCase);

    public async Task<(DeactivationAttempt deactivation, UnloadOutcomeRecord unload)> AttemptUnloadAsync(
        string packageId,
        PackageAssemblyLoadContext context,
        TimeSpan deactivationTimeout,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var requestedAt = DateTimeOffset.UtcNow;
        var completed = false;
        var timedOut = false;

        try
        {
            await Task.Delay(deactivationTimeout, cancellationToken);
            completed = true;
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
        }

        var deactivation = new DeactivationAttempt(
            packageId,
            requestedAt,
            (int)deactivationTimeout.TotalMilliseconds,
            completed,
            timedOut,
            timedOut ? "deactivation-timeout" : "deactivation-complete",
            correlationId);

        var attempt = attempts.AddOrUpdate(packageId, 1, (_, current) => current + 1);

        try
        {
            context.Unload();
            var unload = new UnloadOutcomeRecord(
                packageId,
                attempt,
                DateTimeOffset.UtcNow,
                UnloadOutcome.UnloadPending,
                "unload-best-effort",
                RetryEligible: true,
                correlationId);
            return (deactivation, unload);
        }
        catch (Exception ex)
        {
            var unload = new UnloadOutcomeRecord(
                packageId,
                attempt,
                DateTimeOffset.UtcNow,
                UnloadOutcome.Failed,
                ex.Message,
                RetryEligible: true,
                correlationId);
            return (deactivation, unload);
        }
    }
}
