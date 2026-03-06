using Microsoft.Extensions.Options;
using Nuplane.Runtime.Configuration;

namespace Nuplane.Runtime.Reconciliation;

/// <summary>
/// Implements an exponential backoff retry policy for reconciliation operations.
/// </summary>
public sealed class ReconciliationRetryPolicy(IOptions<ReconciliationOptions> options) : IReconciliationRetryPolicy
{
    private readonly ReconciliationOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

    /// <inheritdoc />
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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                if (attempt > _options.MaxRetryAttempts)
                {
                    throw;
                }
                var backoff = GetBackoffForRetry(_options, attempt);
                await Task.Delay(backoff, cancellationToken);
            }
        }
    }


    /// <summary>
    /// Computes the exponential backoff delay for the specified retry attempt.
    /// </summary>
    /// <param name="options">The reconciliation options containing backoff settings.</param>
    /// <param name="retryAttempt">The 1-based retry attempt number.</param>
    /// <returns>The backoff delay, capped at <see cref="ReconciliationOptions.MaxRetryBackoff"/>.</returns>
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
