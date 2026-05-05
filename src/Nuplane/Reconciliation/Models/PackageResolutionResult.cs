using Nuplane.Abstractions;

namespace Nuplane.Reconciliation.Models;

/// <summary>
/// Contains the result of resolving desired package requests, including resolved packages,
/// failed package identifiers, and per-package feed resolution decisions.
/// </summary>
/// <param name="ResolvedPackages">The packages that were successfully resolved.</param>
/// <param name="FailedPackageIds">The identifiers of packages that failed resolution.</param>
/// <param name="FeedDecisions">The feed resolution decision records for each package.</param>
/// <param name="ResolvedGraphs">The resolved package graphs, when dependency closure metadata is available.</param>
public sealed record PackageResolutionResult(
    IReadOnlyList<ResolvedPackage> ResolvedPackages,
    IReadOnlyList<string> FailedPackageIds,
    IReadOnlyList<FeedResolutionDecision> FeedDecisions,
    IReadOnlyList<ResolvedPackageGraph>? ResolvedGraphs = null)
{
    /// <summary>
    /// Gets the resolved dependency graphs, normalizing legacy construction to an empty list.
    /// </summary>
    public IReadOnlyList<ResolvedPackageGraph> ResolvedGraphs { get; init; } = ResolvedGraphs ?? [];
}
