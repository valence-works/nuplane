using Nuplane.Abstractions;

namespace Nuplane.Reconciliation.Models;

/// <summary>
/// Contains the result of executing transactional package activation.
/// </summary>
/// <param name="AppliedPackages">The packages that were successfully activated.</param>
/// <param name="FailedPackageIds">The identifiers of packages that failed activation.</param>
/// <param name="FailureMessages">Per-package failure messages keyed by package identifier.</param>
public sealed record PackageApplyExecutionResult(
    IReadOnlyList<ResolvedPackage> AppliedPackages,
    IReadOnlyList<string> FailedPackageIds,
    IReadOnlyDictionary<string, string>? FailureMessages = null)
{
    /// <summary>
    /// Gets the graph transactions that completed successfully, preserving each graph's exact
    /// final root requests and applied package projection.
    /// </summary>
    public IReadOnlyList<ResolvedPackageGraphSelection> SuccessfulGraphSelections { get; init; } = [];
}
