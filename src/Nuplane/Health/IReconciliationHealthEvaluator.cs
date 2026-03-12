namespace Nuplane.Health;

/// <summary>
/// Evaluates the health status of the reconciliation system based on failure counts and source freshness.
/// </summary>
public interface IReconciliationHealthEvaluator
{
    /// <summary>
    /// Gets whether the system is currently in a degraded state.
    /// </summary>
    bool IsDegraded { get; }

    /// <summary>
    /// Gets the number of trust policy failures from the last evaluation.
    /// </summary>
    int LastTrustFailureCount { get; }

    /// <summary>
    /// Gets the number of lock file failures from the last evaluation.
    /// </summary>
    int LastLockFailureCount { get; }

    /// <summary>
    /// Gets the number of cleanup failures from the last evaluation.
    /// </summary>
    int LastCleanupFailureCount { get; }

    /// <summary>
    /// Gets the number of manifest read failures from the last evaluation.
    /// </summary>
    int LastManifestFailureCount { get; }

    /// <summary>
    /// Gets the number of source outage events from the last evaluation.
    /// </summary>
    int LastSourceOutageCount { get; }

    /// <summary>
    /// Gets the number of acquisition failures from the last evaluation.
    /// </summary>
    int LastAcquisitionFailureCount { get; }

    /// <summary>
    /// Gets the number of admin trigger rejections from the last evaluation.
    /// </summary>
    int LastAdminRejectionCount { get; }

    /// <summary>
    /// Evaluates the health input and updates the degraded state.
    /// </summary>
    /// <param name="input">The health input data.</param>
    /// <returns><see langword="true"/> if the system is degraded; otherwise <see langword="false"/>.</returns>
    bool Evaluate(ReconciliationHealthInput input);
}
