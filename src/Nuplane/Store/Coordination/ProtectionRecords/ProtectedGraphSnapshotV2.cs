using System.Collections.ObjectModel;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Coordination.ProtectionRecords;

/// <summary>Describes one v2 graph closure using common-generation recovery evidence.</summary>
/// <remarks>This value is persisted description only; it does not grant graph-use or deletion authority.</remarks>
public sealed class ProtectedGraphSnapshotV2
{
    internal ProtectedGraphSnapshotV2(
        Guid snapshotId,
        string graphId,
        string generationId,
        ProtectedGraphDisposition disposition,
        IEnumerable<PhysicalRootIdentity> roots,
        IEnumerable<PackageGraphRootSelection> requestedRoots,
        IEnumerable<PackageGraphNodeIdentity> nodes,
        IEnumerable<PackageGraphEdgeIdentity> edges,
        ProtectedGraphRecoverySelectionEvidenceV2? recoverySelectionEvidence)
    {
        if (snapshotId == Guid.Empty)
            throw new ArgumentException("A graph snapshot identifier cannot be empty.", nameof(snapshotId));
        ArgumentException.ThrowIfNullOrWhiteSpace(graphId);
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        if (disposition == 0 || (disposition & ~ProtectedGraphDisposition.ActiveAndRecoverable) != 0)
            throw new ArgumentOutOfRangeException(nameof(disposition));
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(requestedRoots);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        var isRecoverable = (disposition & ProtectedGraphDisposition.Recoverable) != 0;
        if (isRecoverable != (recoverySelectionEvidence is not null))
            throw new ArgumentException("Recoverable graphs require v2 selection evidence; active-only graphs cannot carry it.", nameof(recoverySelectionEvidence));

        var rootItems = roots.ToArray();
        var requestItems = requestedRoots.ToArray();
        var nodeItems = nodes.ToArray();
        var edgeItems = edges.ToArray();
        if (rootItems.Any(static item => item is null) || requestItems.Any(static item => item is null) ||
            nodeItems.Any(static item => item is null) || edgeItems.Any(static item => item is null))
            throw new ArgumentException("Graph collections cannot contain null items.");

        var copiedRoots = rootItems.Select(ProtectionRecordValueCopies.CopyRoot).ToArray();
        var copiedRequests = requestItems.Select(ProtectionRecordValueCopies.CopyRootSelection).ToArray();
        var copiedNodes = nodeItems.Select(ProtectionRecordValueCopies.CopyNode).ToArray();
        var copiedEdges = edgeItems.Select(ProtectionRecordValueCopies.CopyEdge).ToArray();
        if (copiedRoots.Length == 0 || copiedRoots.Distinct().Count() != copiedRoots.Length)
            throw new ArgumentException("A graph requires a non-empty set of unique physical roots.", nameof(roots));
        if (copiedRequests.Length == 0)
            throw new ArgumentException("A graph requires at least one requested root.", nameof(requestedRoots));
        if (copiedNodes.Length == 0)
            throw new ArgumentException("A graph requires at least one selected package node.", nameof(nodes));

        var nodeIds = copiedNodes.Select(static node => node.NodeId).ToHashSet();
        if (nodeIds.Count != copiedNodes.Length)
            throw new ArgumentException("A graph cannot contain duplicate node identities.", nameof(nodes));
        if (copiedRequests.Any(request => !nodeIds.Contains(request.SelectedNodeId)))
            throw new ArgumentException("A requested root refers to a node missing from the graph.", nameof(requestedRoots));
        if (copiedEdges.Any(edge => !nodeIds.Contains(edge.FromNodeId) || !nodeIds.Contains(edge.ToNodeId)))
            throw new ArgumentException("A dependency edge refers to a node missing from the graph.", nameof(edges));

        var installRoots = copiedNodes.Select(static node => node.Install.Root).Distinct().ToHashSet();
        if (!installRoots.SetEquals(copiedRoots))
            throw new ArgumentException("The graph's physical roots must exactly match the roots of its selected installs.", nameof(roots));

        var evidence = recoverySelectionEvidence?.Copy();
        if (evidence is not null)
        {
            var requestedNodeIds = copiedRequests.Select(static request => request.SelectedNodeId).OrderBy(static nodeId => nodeId).ToArray();
            var evidenceNodeIds = evidence.SelectedRootNodeIds.OrderBy(static nodeId => nodeId).ToArray();
            if (!requestedNodeIds.SequenceEqual(evidenceNodeIds))
                throw new ArgumentException("Recovery evidence must identify exactly the nodes selected for every requested root.", nameof(recoverySelectionEvidence));
        }

        SnapshotId = snapshotId;
        GraphId = graphId;
        GenerationId = generationId;
        Disposition = disposition;
        Roots = new ReadOnlyCollection<PhysicalRootIdentity>(copiedRoots);
        RequestedRoots = new ReadOnlyCollection<PackageGraphRootSelection>(copiedRequests);
        Nodes = new ReadOnlyCollection<PackageGraphNodeIdentity>(copiedNodes);
        Edges = new ReadOnlyCollection<PackageGraphEdgeIdentity>(copiedEdges);
        RecoverySelectionEvidence = evidence;
    }

    /// <summary>Gets the snapshot identity.</summary>
    public Guid SnapshotId { get; }

    /// <summary>Gets the deterministic graph identity.</summary>
    public string GraphId { get; }

    /// <summary>Gets the graph generation identity.</summary>
    public string GenerationId { get; }

    /// <summary>Gets whether this graph is active, recoverable, or both.</summary>
    public ProtectedGraphDisposition Disposition { get; }

    /// <summary>Gets the copied physical roots represented by the selected installs.</summary>
    public IReadOnlyList<PhysicalRootIdentity> Roots { get; }

    /// <summary>Gets the copied original requests and selected graph nodes.</summary>
    public IReadOnlyList<PackageGraphRootSelection> RequestedRoots { get; }

    /// <summary>Gets the copied selected package nodes and exact install identities.</summary>
    public IReadOnlyList<PackageGraphNodeIdentity> Nodes { get; }

    /// <summary>Gets the copied dependency edges between selected nodes.</summary>
    public IReadOnlyList<PackageGraphEdgeIdentity> Edges { get; }

    /// <summary>Gets common-generation selection evidence for a recoverable graph; null for active-only graphs.</summary>
    public ProtectedGraphRecoverySelectionEvidenceV2? RecoverySelectionEvidence { get; }

    internal ProtectedGraphSnapshotV2 Copy()
        => new(SnapshotId, GraphId, GenerationId, Disposition, Roots, RequestedRoots, Nodes, Edges, RecoverySelectionEvidence);

    internal bool HasSamePayloadAs(ProtectedGraphSnapshotV2 other)
        => SnapshotId == other.SnapshotId &&
           string.Equals(GraphId, other.GraphId, StringComparison.Ordinal) &&
           string.Equals(GenerationId, other.GenerationId, StringComparison.Ordinal) &&
           Disposition == other.Disposition &&
           ProtectionRecordValueCopies.HaveSameItems(Roots, other.Roots) &&
           ProtectionRecordValueCopies.HaveSameItems(RequestedRoots, other.RequestedRoots) &&
           ProtectionRecordValueCopies.HaveSameItems(Nodes, other.Nodes) &&
           ProtectionRecordValueCopies.HaveSameItems(Edges, other.Edges) &&
           (RecoverySelectionEvidence is null
               ? other.RecoverySelectionEvidence is null
               : other.RecoverySelectionEvidence is not null && RecoverySelectionEvidence.HasSamePayloadAs(other.RecoverySelectionEvidence));
}
