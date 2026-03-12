using Microsoft.Extensions.Options;
using Nuplane.Reconciliation.Configuration;
using Polly;
using Polly.Retry;
using Polly.Registry;

namespace Nuplane.Reconciliation;

/// <summary>
/// Implements a resilience-pipeline-backed retry policy for reconciliation operations.
/// </summary>
public sealed class ReconciliationRetryPolicy : IReconciliationRetryPolicy
{
    internal const string PipelineName = "nuplane.reconciliation.retry";

    private readonly ResiliencePipeline _pipeline;

    /// <summary>
    /// Initializes a retry policy that resolves its strategy from a named resilience pipeline registered in DI.
    /// </summary>
    /// <param name="options">The reconciliation retry options.</param>
    /// <param name="pipelineProvider">The provider used to resolve the named retry pipeline.</param>
    public ReconciliationRetryPolicy(IOptions<ReconciliationOptions> options, ResiliencePipelineProvider<string> pipelineProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _pipeline = ResolvePipeline(pipelineProvider);
    }

    // Used by tests and direct construction paths where DI is not involved.
    internal ReconciliationRetryPolicy(IOptions<ReconciliationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _pipeline = CreatePipeline(options.Value);
    }

    /// <inheritdoc />
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return await _pipeline.ExecuteAsync(
            static async (callback, token) => await callback(token).ConfigureAwait(false),
            operation,
            cancellationToken).ConfigureAwait(false);
    }

    private static ResiliencePipeline ResolvePipeline(ResiliencePipelineProvider<string> pipelineProvider)
    {
        ArgumentNullException.ThrowIfNull(pipelineProvider);
        return pipelineProvider.GetPipeline(PipelineName);
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
