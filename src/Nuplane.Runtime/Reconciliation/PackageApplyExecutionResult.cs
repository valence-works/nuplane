using Nuplane.Abstractions;

namespace Nuplane.Runtime.Reconciliation;

/// <summary>
/// Contains the result of executing transactional package activation.
/// </summary>
/// <param name="AppliedPackages">The packages that were successfully activated.</param>
/// <param name="FailedPackageIds">The identifiers of packages that failed activation.</param>
public sealed record PackageApplyExecutionResult(
    IReadOnlyList<ResolvedPackage> AppliedPackages,
    IReadOnlyList<string> FailedPackageIds);

