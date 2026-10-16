using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Coordination;

/// <summary>Joins native graph binding, immutable publication and counted lease ownership.</summary>
/// <remarks>
/// This core seam accepts only a complete graph in the supplied root and refuses foreign roots.
/// It does not acquire a root/member lock, deserialize a capability, or perform package payload I/O.
/// </remarks>
internal sealed class PackageGraphUseLeaseAcquisition(
    RootMembershipRegistry? registry,
    IPackageGraphUseLifetimeObserver lifetimeObserver) : IResolvedPackageGraphUseLeaseAcquisition
{
    internal RootMembershipRegistry? Registry => registry;
    internal IPackageGraphUseLifetimeObserver LifetimeObserver => lifetimeObserver;

    public async ValueTask<PackageGraphUseLeaseOwner> AcquireForRootAsync(
        PackageStoreOperationBorrow borrow,
        ResolvedPackageGraph graph,
        IReadOnlyList<PackageRequest> requests,
        PackageGraphUseSnapshotState snapshotState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lifetimeObserver);
        ArgumentNullException.ThrowIfNull(borrow);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(requests);
        if (!Enum.IsDefined(snapshotState))
            throw new ArgumentOutOfRangeException(nameof(snapshotState));
        cancellationToken.ThrowIfCancellationRequested();
        if (registry is null)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "This package-store composition does not provide native graph-use publication.", borrow.Root);
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
                RequireProviderBinding(files, borrow);
                using var binding = PackageGraphUseInstallBinding.Observe(files, registry, root, members.Ledger, copiedGraph.Nodes);
                var paired = PackageGraphUseInstallBinding.CreateActiveCandidate(copiedGraph, copiedRequests, [binding]);
                return await PublishBoundSnapshotAsync(files, root, borrow, paired.Candidate, paired.ExactPathsByNode,
                    snapshotState, binding.Revalidate, record => publication = record, token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            // The callback's final full-member replay must succeed before any read capability escapes.
            owner = PackageGraphUseLeaseOwnerControl.Create(borrow.Root, prepared.Snapshot, prepared.Paths,
                publication ?? throw new InvalidOperationException("Graph-use publication did not return native ownership."),
                lifetimeObserver, registry.Files);
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

    internal ValueTask<PackageGraphUseLeaseOwner> AcquireProtectedRecoveryForRootAsync(
        PackageStoreOperationBorrow borrow,
        ProtectedGraphSnapshot recoverableGraph,
        string configuredInstallRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        ArgumentNullException.ThrowIfNull(recoverableGraph);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredInstallRoot);
        cancellationToken.ThrowIfCancellationRequested();
        if (registry is null)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "This package-store composition does not provide native graph-use publication.", borrow.Root);
        if ((recoverableGraph.Disposition & ProtectedGraphDisposition.Recoverable) == 0 ||
            recoverableGraph.RecoverySelectionEvidence is null)
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                "Recovery acquisition requires the exact persisted recoverable graph candidate.", borrow.Root);
        }

        var activeView = AsActiveView(recoverableGraph);
        return AcquireProtectedGraphForRootAsync(
            borrow,
            activeView,
            (files, root, ledger) => PackageGraphUseProtectedInstallBinding.Observe(
                files, registry!, root, ledger, recoverableGraph, configuredInstallRoot),
            cancellationToken,
            "Recovery graph-use acquisition");
    }

    internal ValueTask<PackageGraphUseLeaseOwner> AcquireProtectedActiveForRootAsync(
        PackageStoreOperationBorrow borrow,
        ProtectedGraphSnapshot activeGraph,
        IReadOnlyDictionary<Guid, string> exactPathsByNode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        ArgumentNullException.ThrowIfNull(activeGraph);
        ArgumentNullException.ThrowIfNull(exactPathsByNode);
        cancellationToken.ThrowIfCancellationRequested();
        if (registry is null)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "This package-store composition does not provide native graph-use publication.", borrow.Root);
        if ((activeGraph.Disposition & ProtectedGraphDisposition.Active) == 0)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                "Active loading requires the exact persisted active graph candidate.", borrow.Root);

        var activeView = AsActiveView(activeGraph);
        return AcquireProtectedGraphForRootAsync(
            borrow,
            activeView,
            (files, root, ledger) => PackageGraphUseProtectedInstallBinding.ObserveActive(
                files, registry!, root, ledger, activeView, exactPathsByNode),
            cancellationToken,
            "Active graph-use acquisition");
    }

    private async ValueTask<PackageGraphUseLeaseOwner> AcquireProtectedGraphForRootAsync(
        PackageStoreOperationBorrow borrow,
        ProtectedGraphSnapshot activeView,
        Func<IPhysicalStoreFileSystem, PhysicalStoreDirectoryHandle, RootMembershipRecord,
            PackageGraphUseProtectedInstallBinding> observeBinding,
        CancellationToken cancellationToken,
        string operationName)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        ArgumentNullException.ThrowIfNull(activeView);
        ArgumentNullException.ThrowIfNull(observeBinding);
        cancellationToken.ThrowIfCancellationRequested();
        if (registry is null)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "This package-store composition does not provide native graph-use publication.", borrow.Root);

        var members = PackageStoreOperationAccess.GetLockedMemberLocations(borrow);
        PublishedGraphUseRecord? publication = null;
        PackageGraphUseLeaseOwner? owner = null;
        try
        {
            var prepared = await PackageStoreOperationAccess.WithValidatedRootAsync(borrow, async (files, root, token) =>
            {
                RequireProviderBinding(files, borrow);
                using var binding = observeBinding(files, root, members.Ledger);
                return await PublishBoundSnapshotAsync(files, root, borrow, activeView, binding.ExactPathsByNode,
                    PackageGraphUseSnapshotState.Committed, binding.Revalidate,
                    record => publication = record, token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            owner = PackageGraphUseLeaseOwnerControl.Create(borrow.Root, prepared.Snapshot, prepared.Paths,
                publication ?? throw new InvalidOperationException("Graph-use publication did not return native ownership."),
                lifetimeObserver, registry.Files);
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
                throw new AggregateException($"{operationName} failed and unreturned ownership did not release cleanly.",
                    primary, cleanup);
            }
            throw;
        }
    }

    private static ProtectedGraphSnapshot AsActiveView(ProtectedGraphSnapshot graph) => new(
        graph.SnapshotId,
        graph.GraphId,
        graph.GenerationId,
        ProtectedGraphDisposition.Active,
        graph.Roots,
        graph.RequestedRoots,
        graph.Nodes,
        graph.Edges,
        recoverySelectionEvidence: null);

    private async Task<PreparedGraphUse> PublishBoundSnapshotAsync(
        IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle root,
        PackageStoreOperationBorrow borrow,
        ProtectedGraphSnapshot candidate,
        IReadOnlyDictionary<Guid, string> paths,
        PackageGraphUseSnapshotState snapshotState,
        Action revalidateBinding,
        Action<PublishedGraphUseRecord> takePublicationOwnership,
        CancellationToken cancellationToken)
    {
        var snapshot = new PackageGraphUseSnapshot(candidate.SnapshotId, candidate.GraphId, candidate.GenerationId,
            snapshotState, candidate.Roots, candidate.RequestedRoots, candidate.Nodes, candidate.Edges);
        var publication = await new PackageGraphUseRecordStore(files).PublishAsync(root, borrow.Root, borrow.Epoch,
            candidate, snapshotState, cancellationToken).ConfigureAwait(false);
        // The caller owns the publication from this point, including if revalidation or the
        // enclosing full-member replay fails after the record is already visible.
        takePublicationOwnership(publication);
        revalidateBinding();
        cancellationToken.ThrowIfCancellationRequested();
        return new PreparedGraphUse(snapshot, paths);
    }

    private void RequireProviderBinding(IPhysicalStoreFileSystem files, PackageStoreOperationBorrow borrow)
    {
        if (!ReferenceEquals(files, registry!.Files))
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "The graph-use acquisition service does not share the admitted root's native filesystem provider.", borrow.Root);
    }

    private sealed record PreparedGraphUse(
        PackageGraphUseSnapshot Snapshot,
        IReadOnlyDictionary<Guid, string> Paths);
}
