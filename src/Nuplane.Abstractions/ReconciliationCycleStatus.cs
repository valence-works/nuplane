namespace Nuplane.Abstractions;

/// <summary>
/// Status of a reconciliation cycle outcome.
/// </summary>
public enum ReconciliationCycleStatus
{
    /// <summary>Cycle completed successfully with all packages converged.</summary>
    Succeeded,

    /// <summary>Cycle completed but with degraded outcomes for some packages or sources.</summary>
    Degraded,

    /// <summary>Cycle failed without mutating any state.</summary>
    FailedNonMutating
}