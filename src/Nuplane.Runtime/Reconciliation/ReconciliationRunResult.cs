using Nuplane.Abstractions;

namespace Nuplane.Runtime.Reconciliation;

/// <summary>
/// Represents the result of a complete reconciliation cycle.
/// </summary>
/// <param name="Skipped">Whether the cycle was skipped (e.g., due to single-flight protection).</param>
/// <param name="ChangeSet">The package change set produced by the cycle.</param>
/// <param name="FailedPackages">The identifiers of packages that failed during the cycle.</param>
/// <param name="IsDegraded">Whether the cycle completed in a degraded state.</param>
public sealed record ReconciliationRunResult(
    bool Skipped,
    PackageChangeSet ChangeSet,
    IReadOnlyList<string> FailedPackages,
    bool IsDegraded);

