using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Maintenance;

/// <summary>Copies descriptive external evidence into structurally validated core candidates.</summary>
/// <remarks>This grants no native authority; enrollment independently verifies source state and every install.</remarks>
internal static class PackageStoreRecoveryGraphFactory
{
    internal static ProtectedGraphSnapshot Create(
        Guid snapshotId, string graphId, string generationId, ProtectedGraphDisposition disposition,
        IEnumerable<PhysicalRootIdentity> roots,
        IEnumerable<(PackageRequest Request, Guid SelectedNodeId)> requests,
        IEnumerable<(Guid NodeId, PackageInstallIdentity Install)> nodes,
        IEnumerable<(Guid FromNodeId, Guid ToNodeId, string PackageId, string VersionRange, string Framework, bool Optional)> edges,
        ProtectedGraphRecoverySelectionEvidence? selection)
        => new(snapshotId, graphId, generationId, disposition, roots,
            requests.Select(static request => new PackageGraphRootSelection(request.Request, request.SelectedNodeId)),
            nodes.Select(static node => new PackageGraphNodeIdentity(node.NodeId, node.Install)),
            edges.Select(static edge => new PackageGraphEdgeIdentity(edge.FromNodeId, edge.ToNodeId,
                edge.PackageId, edge.VersionRange, edge.Framework, edge.Optional)), selection);
}
