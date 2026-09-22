using Nuplane.Abstractions;

namespace Nuplane.Capabilities;

/// <summary>
/// Carries the capability contributions one reconciliation cycle refused for not being pinned out
/// of that cycle, so a host-free restore can report them.
/// </summary>
/// <remarks>
/// <para>
/// A desired request's pinned-ness is answerable before a cycle runs, which is why
/// <c>NuplaneRestore.RestoreAsync</c> can refuse one outright. A contributed request's is not: its
/// declaring package must be acquired before its <c>nuplane.json</c> can be read. The refusal
/// therefore happens inside the cycle, and its subject — the offending request, not just the
/// declaring package's id — has no other way back to the caller: a cycle reports failures as package
/// ids, and <c>ReconciliationRunResult</c> carries no request.
/// </para>
/// <para>
/// Only the latest cycle is kept, keyed by correlation id: a new correlation replaces the previous
/// cycle's entries rather than accumulating across cycles, which is what makes a long-lived host's
/// ledger bounded and a restore's single cycle unambiguous. <c>CapabilityDesiredStateContributor</c>
/// records on every contribution round, so an empty round for a new correlation clears the previous
/// cycle even when nothing was refused.
/// </para>
/// <para>
/// <b>Precondition: cycles do not overlap.</b> "The latest cycle" is a well-defined thing to report
/// only because a store has one writer at a time.
/// <c>ReconciliationOptions.EnableSingleFlight</c> serializes cycles inside one reconciliation
/// service and <c>ReconciliationOptions.EnableStoreLock</c> serializes them across processes; both
/// default on, and a host-free restore runs exactly one cycle inside that lock, which is the caller
/// that reads this. A host that switches both off gets last-writer-wins: every operation here is
/// still taken under a lock, so two interleaved cycles can never tear the list or read a
/// half-written one, but the list then describes whichever cycle wrote last rather than the cycle
/// the reader has in hand. What cannot happen either way is a stale entry passing for a new cycle's:
/// a new correlation id clears what the previous one left, so a cycle that refused nothing reports
/// nothing.
/// </para>
/// </remarks>
internal sealed class CapabilityContributionLedger
{
    private readonly object _gate = new();
    private readonly List<PackageRequest> _unpinnedRequests = [];
    private string? _correlationId;

    /// <summary>
    /// Replaces the recorded requests when <paramref name="correlationId"/> names a new cycle, then
    /// adds the ones this round refused that are not recorded yet.
    /// </summary>
    /// <param name="correlationId">The cycle this round belongs to.</param>
    /// <param name="unpinnedRequests">The contributions this round refused for not naming a single version.</param>
    internal void RecordUnpinned(string correlationId, IReadOnlyList<PackageRequest> unpinnedRequests)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentNullException.ThrowIfNull(unpinnedRequests);

        lock (_gate)
        {
            if (!string.Equals(_correlationId, correlationId, StringComparison.Ordinal))
            {
                _correlationId = correlationId;
                _unpinnedRequests.Clear();
            }

            foreach (var request in unpinnedRequests.Where(request => !_unpinnedRequests.Contains(request)))
            {
                _unpinnedRequests.Add(request);
            }
        }
    }

    /// <summary>
    /// The contributions the latest cycle refused for not naming a single version, ordered by
    /// package id then source name. Empty when no cycle refused one.
    /// </summary>
    internal IReadOnlyList<PackageRequest> UnpinnedRequests
    {
        get
        {
            lock (_gate)
            {
                return _unpinnedRequests
                    .OrderBy(static request => request.Id, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static request => request.SourceName, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }
}
