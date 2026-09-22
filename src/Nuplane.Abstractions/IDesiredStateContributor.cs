namespace Nuplane.Abstractions;

/// <summary>
/// Contributes additional desired root packages that only the packages already resolved in a cycle
/// can ask for, and refuses the packages whose additional requirement cannot be met.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IDesiredPackageSource"/> answers "what does the host want?" before anything is
/// resolved. A contributor answers the question that can only be asked afterwards: "what do the
/// packages now on disk additionally require?". It is therefore called during resolution, after the
/// desired roots have been resolved and their dependency closure expanded, with every resolved
/// package's install path available — which is what lets a contributor read package-carried
/// metadata, the way <c>CapabilityDesiredStateContributor</c> reads each package's
/// <c>nuplane.json</c>.
/// </para>
/// <para>
/// A contributed request becomes an ordinary root: it is resolved with the same retry policy,
/// recorded with the same failure stages, and then flows through graph selection, lock-file
/// evaluation, the trust gate, the diff, and the transactions exactly like a root a desired source
/// asked for. Contributors are invoked repeatedly until a round contributes nothing new, because a
/// contributed package may itself require something; the number of rounds is bounded, and an
/// overrun is refused rather than silently truncated.
/// </para>
/// <para>
/// Implementations must be deterministic — the same context must produce the same contribution, in
/// the same order — and must not mutate package or store state. An implementation that throws fails
/// the reconciliation cycle: a contributor whose requirement could not even be computed must not be
/// mistaken for one that had nothing to add.
/// </para>
/// </remarks>
public interface IDesiredStateContributor
{
    /// <summary>
    /// Returns the additional roots and the refusals this contributor derives from
    /// <paramref name="context"/>.
    /// </summary>
    /// <param name="context">The cycle's correlation, its desired requests, the packages resolved so far, and the requests earlier rounds contributed.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The contribution, or <see cref="DesiredStateContribution.Nothing"/> when this contributor has nothing to add.</returns>
    Task<DesiredStateContribution> ContributeAsync(DesiredStateContributionContext context, CancellationToken ct);
}
