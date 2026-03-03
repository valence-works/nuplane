using Nuplane.Abstractions;

namespace Nuplane.Runtime.Reconciliation.Models;

/// <summary>
/// Contains the result of resolving desired package requests, including resolved packages,
/// failed package identifiers, and per-package feed resolution decisions.
/// </summary>
/// <param name="ResolvedPackages">The packages that were successfully resolved.</param>
/// <param name="FailedPackageIds">The identifiers of packages that failed resolution.</param>
/// <param name="FeedDecisions">The feed resolution decision records for each package.</param>
public sealed record PackageResolutionResult(
    IReadOnlyList<ResolvedPackage> ResolvedPackages,
    IReadOnlyList<string> FailedPackageIds,
    IReadOnlyList<FeedResolutionDecision> FeedDecisions);

