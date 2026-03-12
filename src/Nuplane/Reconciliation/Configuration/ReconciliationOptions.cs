namespace Nuplane.Reconciliation.Configuration;

/// <summary>
/// Configuration options controlling reconciliation cycle behavior, including poll interval,
/// single-flight protection, and exponential backoff retry settings.
/// </summary>
public sealed class ReconciliationOptions
{
    /// <summary>
    /// Gets or sets the interval between automatic reconciliation cycles.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets whether automatic background reconciliation is enabled.
    /// When <see langword="true"/>, a hosted service polls at <see cref="PollInterval"/> intervals.
    /// Defaults to <see langword="false"/> (manual-only).
    /// </summary>
    public bool EnableAutomaticReconciliation { get; set; } = false;

    /// <summary>
    /// Gets or sets whether only one reconciliation cycle is allowed to execute at a time.
    /// </summary>
    public bool EnableSingleFlight { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of retry attempts for transient failures during reconciliation.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Gets or sets the initial delay before the first retry attempt.
    /// </summary>
    public TimeSpan InitialRetryBackoff { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or sets the maximum delay between retry attempts (caps exponential backoff).
    /// </summary>
    public TimeSpan MaxRetryBackoff { get; set; } = TimeSpan.FromSeconds(30);
}
