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
    /// Gets or sets whether a reconciliation cycle takes an exclusive lock on the store it writes,
    /// so that no other process reconciles the same <c>store-state.json</c> at the same time.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="EnableSingleFlight"/> serializes cycles inside one reconciliation service; this
    /// serializes them across processes. Without it, two hosts — or a host and an out-of-process
    /// restore — can interleave their read-modify-write of the state file and tear it, which is
    /// silent until a later read fails to parse.
    /// </para>
    /// <para>
    /// It defaults on because the failure it prevents is silent corruption that outlives the
    /// process, while the behaviour it introduces is a reported, retryable skip:
    /// <see cref="Models.ReconciliationRunResult.Skipped"/> with
    /// <see cref="Models.ReconciliationSkipReason.StoreLockUnavailable"/>, plus a warning. A store
    /// with a single writer never contends, so its behaviour is unchanged; a store whose lock file
    /// cannot be created is reconciled unprotected rather than refused, so a deployment that works
    /// today keeps working. Switch it off only to restore the pre-lock behaviour exactly.
    /// </para>
    /// <para>
    /// In-memory stores have no file to lock and are unaffected.
    /// </para>
    /// </remarks>
    public bool EnableStoreLock { get; set; } = true;

    /// <summary>
    /// Gets or sets how startup reconciliation failures affect host startup.
    /// Defaults to failing host startup when required startup reconciliation fails.
    /// </summary>
    public StartupFailurePolicy StartupFailurePolicy { get; set; } = StartupFailurePolicy.FailHost;

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
