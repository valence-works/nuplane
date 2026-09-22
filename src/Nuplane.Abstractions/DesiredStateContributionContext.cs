namespace Nuplane.Abstractions;

/// <summary>
/// Everything an <see cref="IDesiredStateContributor"/> is given for one round: the cycle's
/// identity, what the host asked for, what is already on disk, and what earlier rounds contributed.
/// </summary>
/// <param name="CorrelationId">The reconciliation cycle's correlation identifier, used when recording failures and logs.</param>
/// <param name="DesiredRequests">
/// The requests the desired-state sources produced — the host's explicit roots. A contributor treats
/// these as decisions already made: a root the host named by hand wins over anything a contributor
/// would otherwise add for the same package.
/// </param>
/// <param name="ResolvedPackages">
/// Every package resolved so far in this cycle, roots and dependencies alike, each with the
/// <see cref="ResolvedPackage.InstallPath"/> its metadata can be read from.
/// </param>
/// <param name="ContributedSoFar">
/// The requests earlier rounds of this cycle already contributed and resolved. A contributor does
/// not have to filter against them — a request for a package that is already a root is ignored —
/// but they make the chain that led to this round visible.
/// </param>
public sealed record DesiredStateContributionContext(
    string CorrelationId,
    IReadOnlyList<PackageRequest> DesiredRequests,
    IReadOnlyList<ResolvedPackage> ResolvedPackages,
    IReadOnlyList<PackageRequest> ContributedSoFar);
