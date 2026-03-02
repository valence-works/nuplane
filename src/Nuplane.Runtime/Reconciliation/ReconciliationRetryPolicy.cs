using Nuplane.Runtime.Configuration;

namespace Nuplane.Runtime.Reconciliation;

public sealed class ReconciliationRetryPolicy
{
    private readonly ReconciliationOptions options;

    public ReconciliationRetryPolicy(ReconciliationOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                return await operation(cancellationToken);
            }
            catch
            {
                if (attempt >= options.MaxRetryAttempts)
                {
                    throw;
                }
                var backoff = GetBackoffForRetry(options, attempt);
                await Task.Delay(backoff, cancellationToken);
            }
        }
    }

    public Task<T> ExecuteForFeedResolutionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        ExecuteAsync(operation, cancellationToken);

    public Task<T> ExecuteForLockEvaluationAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        ExecuteAsync(operation, cancellationToken);

    public Task<T> ExecuteForDryRunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        ExecuteAsync(operation, cancellationToken);

    public static TimeSpan GetBackoffForRetry(ReconciliationOptions options, int retryAttempt)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (retryAttempt <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAttempt));
        }

        var multiplier = Math.Pow(2, retryAttempt - 1);
        var computed = TimeSpan.FromMilliseconds(options.InitialRetryBackoff.TotalMilliseconds * multiplier);
        return computed <= options.MaxRetryBackoff ? computed : options.MaxRetryBackoff;
    }
}
