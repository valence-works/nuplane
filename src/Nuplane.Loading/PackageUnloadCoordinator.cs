using System.Collections.Concurrent;

namespace Nuplane.Loading;

public sealed class PackageUnloadCoordinator : IPackageUnloadCoordinator
{
    private readonly ConcurrentDictionary<string, int> attempts = new(StringComparer.OrdinalIgnoreCase);

    public Task<(DeactivationAttempt deactivation, UnloadOutcomeRecord unload)> AttemptUnloadAsync(
        string packageId,
        PackageAssemblyLoadContext context,
        TimeSpan deactivationTimeout,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        return AttemptUnloadAsync(
            packageId,
            new PackageLoadContextHandle($"{packageId}:context", context),
            deactivationTimeout,
            correlationId,
            cancellationToken);
    }

    public async Task<(DeactivationAttempt deactivation, UnloadOutcomeRecord unload)> AttemptUnloadAsync(
        string packageId,
        PackageLoadContextHandle context,
        TimeSpan deactivationTimeout,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        if (context.Context is not PackageAssemblyLoadContext loadContext)
        {
            throw new ArgumentException("Invalid load context handle.", nameof(context));
        }

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
            loadContext.Unload();
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
