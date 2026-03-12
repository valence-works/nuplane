using Microsoft.Extensions.Options;
using Nuplane.Reconciliation.Configuration;
using Polly;
using Polly.Retry;

namespace Nuplane.Reconciliation;

/// <summary>
/// Implements a resilience-pipeline-backed retry policy for reconciliation operations.
/// </summary>
public sealed class ReconciliationRetryPolicy(IOptions<ReconciliationOptions> options) : IReconciliationRetryPolicy
{
    private readonly ReconciliationOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly ResiliencePipeline _pipeline = CreatePipeline((options ?? throw new ArgumentNullException(nameof(options))).Value);

    /// <inheritdoc />
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return await _pipeline.ExecuteAsync(
            static async (callback, token) => await callback(token).ConfigureAwait(false),
            operation,
            cancellationToken).ConfigureAwait(false);
    }

    internal static ResiliencePipeline CreatePipeline(ReconciliationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = new ResiliencePipelineBuilder();

        if (options.MaxRetryAttempts == 0)
        {
            return builder.Build();
        }

        return builder
            .AddRetry(CreateRetryOptions(options))
            .Build();
    }

    internal static RetryStrategyOptions CreateRetryOptions(ReconciliationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new RetryStrategyOptions
        {
            MaxRetryAttempts = options.MaxRetryAttempts,
            Delay = options.InitialRetryBackoff,
            MaxDelay = options.MaxRetryBackoff,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            ShouldHandle = new PredicateBuilder()
                .Handle<Exception>(static ex => ex is not OperationCanceledException)
        };
    }

    /// <summary>
    /// Computes the deterministic exponential backoff delay for the specified retry attempt before jitter is applied.
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
