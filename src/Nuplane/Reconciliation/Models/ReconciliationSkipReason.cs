namespace Nuplane.Reconciliation.Models;

/// <summary>
/// Why a reconciliation cycle did nothing. Reported on
/// <see cref="ReconciliationRunResult.SkipReason"/> whenever
/// <see cref="ReconciliationRunResult.Skipped"/> is <see langword="true"/>, so that "another cycle
/// is already running in this process" and "another process owns this store" can be told apart.
/// </summary>
public enum ReconciliationSkipReason
{
    /// <summary>
    /// The cycle was not skipped.
    /// </summary>
    None,

    /// <summary>
    /// Single-flight protection declined the cycle because another cycle was already running in
    /// this reconciliation service.
    /// </summary>
    SingleFlight,

    /// <summary>
    /// The store lock could not be taken because another process — or another Nuplane composition
    /// in this process — is reconciling the same store. Nothing was read, resolved, or written.
    /// </summary>
    StoreLockUnavailable
}
