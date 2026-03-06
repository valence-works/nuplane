namespace Nuplane.Runtime.Configuration;

/// <summary>
/// Bounded retry/backoff configuration for convergence operations.
/// </summary>
public sealed class ConvergenceRetryOptions
{
    /// <summary>
    /// Gets or sets the maximum number of retry attempts.
    /// Defaults to 3.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// Gets or sets the initial delay before the first retry attempt.
    /// Defaults to 2 seconds.
    /// </summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or sets the maximum delay between retry attempts (caps exponential backoff).
    /// Defaults to 30 seconds.
    /// </summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);
}