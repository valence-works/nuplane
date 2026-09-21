using Nuplane.Abstractions;

namespace Nuplane.Reconciliation.Models;

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
    bool IsDegraded)
{
    /// <summary>
    /// Gets why the cycle was skipped, or <see cref="ReconciliationSkipReason.None"/> when it ran.
    /// Declared outside the primary constructor deliberately: every existing four-argument
    /// construction and deconstruction of this record keeps compiling and keeps binding.
    /// </summary>
    public ReconciliationSkipReason SkipReason { get; init; } = ReconciliationSkipReason.None;
}

