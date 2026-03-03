using System.Collections.Concurrent;

namespace Nuplane.Loading;

/// <summary>
/// Coordinates the unloading of package assembly load contexts, including deactivation
/// timeout management, GC-assisted unload verification, and retry tracking.
/// </summary>
public sealed class PackageUnloadCoordinator : IPackageUnloadCoordinator
{
    private readonly ConcurrentDictionary<string, int> attempts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Attempts to unload a package assembly load context directly.
    /// </summary>
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

    /// <inheritdoc />
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

        var sessionKey = context.ContextKey;
        var requestedAt = DateTimeOffset.UtcNow;
        var completed = false;
        var timedOut = false;

        using var timeoutCts = new CancellationTokenSource(deactivationTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, linkedCts.Token);
            completed = true;
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw; // Real cancellation from host shutdown, propagate
            }

            // Timeout CTS fired — deactivation timeout elapsed
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

        var attempt = attempts.AddOrUpdate(sessionKey, 1, (_, current) => current + 1);

        try
        {
            var weakReference = new WeakReference(loadContext);

            loadContext.Unload();
            loadContext = null!;

            const int maxUnloadChecks = 10;
            for (var i = 0; i < maxUnloadChecks && weakReference.IsAlive; i++)
            {
                await Task.Delay(50, CancellationToken.None);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            var outcome = weakReference.IsAlive ? UnloadOutcome.UnloadPending : UnloadOutcome.Unloaded;
            var reason = weakReference.IsAlive ? "unload-pending" : "unload-complete";
            var retryEligible = weakReference.IsAlive;

            var unload = new UnloadOutcomeRecord(
                packageId,
                attempt,
                DateTimeOffset.UtcNow,
                outcome,
                reason,
                RetryEligible: retryEligible,
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
