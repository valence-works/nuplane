using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Reconciliation;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Loading;

/// <summary>Joins a persisted recovery graph and its package projections to one committed native lease.</summary>
internal static class ScopedProtectedRecoveryGraphValidator
{
    internal static void Validate(LeasedProtectedRecoveryGraphSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var graph = selection.Graph;
        var owner = selection.LeaseOwner;
        var leaseSnapshot = owner.Lease.Snapshot;

        if ((graph.Disposition & ProtectedGraphDisposition.Recoverable) == 0 ||
            graph.RecoverySelectionEvidence is not { } evidence ||
            !string.Equals(evidence.RecoveryPolicyId, RecoverableGraphSnapshotFactory.RecoveryPolicyId, StringComparison.Ordinal) ||
            evidence.SourceRevision <= 0 ||
            leaseSnapshot.State != PackageGraphUseSnapshotState.Committed ||
            !string.Equals(graph.GraphId, leaseSnapshot.GraphId, StringComparison.Ordinal) ||
            !string.Equals(graph.GenerationId, leaseSnapshot.GenerationId, StringComparison.Ordinal) ||
            graph.SnapshotId != leaseSnapshot.SnapshotId ||
            !graph.Roots.SequenceEqual(leaseSnapshot.Roots) ||
            !graph.RequestedRoots.SequenceEqual(leaseSnapshot.RequestedRoots) ||
            !graph.Nodes.SequenceEqual(leaseSnapshot.Nodes) ||
            !graph.Edges.SequenceEqual(leaseSnapshot.Edges))
        {
            throw Refused(PackageStoreAdmissionReason.StateMismatch,
                "The persisted recovery graph does not match its exact committed active-only graph-use publication.", leaseSnapshot);
        }

        if (graph.Roots.Count != 1 || graph.Nodes.Count == 0 || graph.RequestedRoots.Count == 0 ||
            graph.Nodes.Any(node => node.Install.Root != graph.Roots[0]) ||
            !graph.RequestedRoots.Select(static request => request.SelectedNodeId)
                .OrderBy(static nodeId => nodeId)
                .SequenceEqual(evidence.SelectedRootNodeIds.OrderBy(static nodeId => nodeId)))
        {
            throw Refused(PackageStoreAdmissionReason.StateMismatch,
                "The recovery graph is incomplete, spans roots, or does not match its versioned root-selection evidence.", leaseSnapshot);
        }

        var nodesById = new Dictionary<Guid, PackageGraphNodeIdentity>();
        var nodesByPackage = new Dictionary<string, PackageGraphNodeIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.Nodes)
        {
            if (node is null || node.NodeId == Guid.Empty || node.Install is null ||
                !nodesById.TryAdd(node.NodeId, node) || !nodesByPackage.TryAdd(node.Install.PackageId, node))
            {
                throw Refused(PackageStoreAdmissionReason.StateMismatch,
                    "The persisted recovery graph contains a duplicate or invalid install node.", leaseSnapshot);
            }
        }

        if (!graph.RequestedRoots.All(request => nodesById.TryGetValue(request.SelectedNodeId, out var node) &&
                string.Equals(request.Request.Id, node.Install.PackageId, StringComparison.OrdinalIgnoreCase)) ||
            graph.Edges.Any(edge => !nodesById.ContainsKey(edge.FromNodeId) ||
                !nodesById.TryGetValue(edge.ToNodeId, out var target) ||
                !string.Equals(edge.RequestedPackageId, target.Install.PackageId, StringComparison.OrdinalIgnoreCase)))
        {
            throw Refused(PackageStoreAdmissionReason.StateMismatch,
                "The persisted recovery graph has a root or dependency edge outside its exact node set.", leaseSnapshot);
        }

        if (selection.Packages.Count != nodesByPackage.Count)
        {
            throw Refused(PackageStoreAdmissionReason.StateMismatch,
                "The package projections do not exactly cover the persisted recovery graph.", leaseSnapshot);
        }

        var seenPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in selection.Packages)
        {
            if (package is null || !seenPackages.Add(package.Id) ||
                !nodesByPackage.TryGetValue(package.Id, out var node) ||
                !string.Equals(package.Id, node.Install.PackageId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(package.Version, node.Install.Version, StringComparison.Ordinal) ||
                !string.Equals(package.PackageContentHash, node.Install.VerifiedArchiveHash, StringComparison.Ordinal))
            {
                throw Refused(PackageStoreAdmissionReason.StateMismatch,
                    "A recovery package projection differs from its persisted package identity.", leaseSnapshot);
            }

            PackageInstallIdentity leaseIdentity;
            try
            {
                leaseIdentity = owner.Lease.GetInstallIdentityForExactPath(package.InstallPath);
            }
            catch (PackageStoreAdmissionException exception)
            {
                throw Refused(exception.Reason,
                    "A recovery package path is outside its published graph-use lease.", leaseSnapshot, exception);
            }
            if (leaseIdentity != node.Install)
            {
                throw Refused(PackageStoreAdmissionReason.RootMismatch,
                    "A recovery package path is not the exact native path bound to its persisted install identity.", leaseSnapshot);
            }
        }
    }

    private static PackageStoreAdmissionException Refused(
        PackageStoreAdmissionReason reason,
        string message,
        PackageGraphUseSnapshot snapshot,
        Exception? inner = null)
        => new(reason, message, snapshot.Roots.Count == 1 ? snapshot.Roots[0] : null, inner);
}
