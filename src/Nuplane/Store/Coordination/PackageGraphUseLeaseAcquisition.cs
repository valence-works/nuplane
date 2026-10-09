using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Store.Coordination;

/// <summary>Joins native graph binding, immutable publication and counted lease ownership.</summary>
/// <remarks>
/// This core seam accepts only a complete graph in the supplied root and refuses foreign roots.
/// It does not acquire a root/member lock, deserialize a capability, or perform package payload I/O.
/// </remarks>
internal sealed class PackageGraphUseLeaseAcquisition(
    RootMembershipRegistry registry,
    IPackageGraphUseLifetimeObserver lifetimeObserver)
{
    internal async ValueTask<PackageGraphUseLeaseOwner> AcquireForRootAsync(
        PackageStoreOperationBorrow borrow,
        ResolvedPackageGraph graph,
        IReadOnlyList<PackageRequest> requests,
        PackageGraphUseSnapshotState snapshotState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(lifetimeObserver);
        ArgumentNullException.ThrowIfNull(borrow);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(requests);
        if (!Enum.IsDefined(snapshotState))
            throw new ArgumentOutOfRangeException(nameof(snapshotState));
        cancellationToken.ThrowIfCancellationRequested();
        var copiedGraph = graph with
        {
            Roots = graph.Roots.ToArray(),
            Nodes = graph.Nodes.ToArray(),
            Edges = graph.Edges.ToArray(),
            SourceDecisions = graph.SourceDecisions.Select(static decision => decision with
            {
                CandidateFeeds = decision.CandidateFeeds.ToArray()
            }).ToArray()
        };
        var copiedRequests = requests.ToArray();
        var members = PackageStoreOperationAccess.GetLockedMemberLocations(borrow);
        PublishedGraphUseRecord? publication = null;
        PackageGraphUseLeaseOwner? owner = null;
        try
        {
            var prepared = await PackageStoreOperationAccess.WithValidatedRootAsync(borrow, async (files, root, token) =>
            {
                using var binding = PackageGraphUseInstallBinding.Observe(files, registry, root, members.Ledger, copiedGraph.Nodes);
                var paired = PackageGraphUseInstallBinding.CreateActiveCandidate(copiedGraph, copiedRequests, [binding]);
                var candidate = paired.Candidate;
                var snapshot = new PackageGraphUseSnapshot(candidate.SnapshotId, candidate.GraphId, candidate.GenerationId,
                    snapshotState, candidate.Roots, candidate.RequestedRoots, candidate.Nodes, candidate.Edges);
                publication = await new PackageGraphUseRecordStore(files).PublishAsync(root, borrow.Root, borrow.Epoch,
                    candidate, snapshotState, token).ConfigureAwait(false);
                binding.Revalidate();
                return (Snapshot: snapshot, Paths: paired.ExactPathsByNode);
            }, cancellationToken).ConfigureAwait(false);

            // The callback's final full-member replay must succeed before any read capability escapes.
            owner = PackageGraphUseLeaseOwnerControl.Create(borrow.Root, prepared.Snapshot, prepared.Paths,
                publication ?? throw new InvalidOperationException("Graph-use publication did not return native ownership."),
                lifetimeObserver);
            publication = null;
            cancellationToken.ThrowIfCancellationRequested();
            return owner;
        }
        catch (Exception primary)
        {
            try
            {
                if (owner is not null)
                    await owner.DisposeAsync().ConfigureAwait(false);
                else if (publication is not null)
                    await publication.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanup)
            {
                throw new AggregateException("Graph-use acquisition failed and unreturned ownership did not release cleanly.",
                    primary, cleanup);
            }
            throw;
        }
    }
}
