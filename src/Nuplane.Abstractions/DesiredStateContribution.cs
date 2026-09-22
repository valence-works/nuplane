namespace Nuplane.Abstractions;

/// <summary>
/// What one <see cref="IDesiredStateContributor"/> round produced: the roots to add, and the
/// already-resolved packages whose requirement cannot be met.
/// </summary>
/// <param name="Requests">
/// The additional roots, each carrying the packages it exists for. A request for a package that is
/// already a root — because the host named it explicitly, or an earlier round contributed it — is
/// ignored, so a contributor never has to inspect the current root set to stay idempotent.
/// </param>
/// <param name="Refusals">
/// The packages that cannot be applied, with the stage and message recorded against each. A refused
/// package is dropped from the root set, its failure is recorded, and the cycle is degraded.
/// </param>
public sealed record DesiredStateContribution(
    IReadOnlyList<ContributedPackageRequest> Requests,
    IReadOnlyList<ContributionRefusal> Refusals)
{
    /// <summary>A contribution that adds nothing and refuses nothing.</summary>
    public static DesiredStateContribution Nothing { get; } = new([], []);
}

/// <summary>
/// One contributed root together with the already-resolved packages it exists for.
/// </summary>
/// <param name="Request">The root request, whose <see cref="PackageRequest.SourceName"/> says which contributor and which decision produced it.</param>
/// <param name="DeclaringPackageIds">
/// The identifiers of the resolved packages whose requirement this request satisfies. They are what
/// gets failed when the request cannot be acquired: a package whose additional requirement is
/// missing is broken, and applying it would only move the failure to load time. Empty means "no
/// package is broken if this cannot be acquired", so nothing but the request itself is failed.
/// </param>
public sealed record ContributedPackageRequest(
    PackageRequest Request,
    IReadOnlyList<string> DeclaringPackageIds);

/// <summary>
/// One already-resolved package a contributor refuses, and why.
/// </summary>
/// <param name="PackageId">The refused package's identifier, as the failure is recorded and reported under.</param>
/// <param name="Stage">The reconciliation stage name the failure is recorded under, for example <c>capability-unselected</c>.</param>
/// <param name="Message">A message naming what is missing and what the operator can do about it.</param>
public sealed record ContributionRefusal(
    string PackageId,
    string Stage,
    string Message);
