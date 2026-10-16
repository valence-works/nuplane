using System.Collections.ObjectModel;
using Nuplane.Abstractions;

namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>
/// Holds an immutable copy of one complete graph-use selection validated by Nuplane core.
/// </summary>
/// <remarks>
/// The constructor is internal so callers cannot assert graph completeness or mint a validated
/// snapshot. A snapshot may contain installs from multiple physical roots; each node carries its own
/// root identity. Root-specific lease providers validate the relevant roots against their borrows.
/// </remarks>
public sealed class PackageGraphUseSnapshot
{
    /// <summary>Initializes a graph-use snapshot after core has validated its complete graph.</summary>
    /// <param name="snapshotId">The non-empty snapshot identity.</param>
    /// <param name="graphId">The deterministic identity of the selected package graph.</param>
    /// <param name="generationId">The graph generation identity.</param>
    /// <param name="state">Whether the snapshot is pending or committed.</param>
    /// <param name="roots">The physical roots represented in the graph.</param>
    /// <param name="requestedRoots">The original requested roots and their selected graph nodes.</param>
    /// <param name="nodes">The selected package nodes.</param>
    /// <param name="edges">The selected dependency edges.</param>
    /// <exception cref="ArgumentException">An identity is empty or a supplied collection contains a null item.</exception>
    /// <exception cref="ArgumentNullException">A supplied collection is null.</exception>
    internal PackageGraphUseSnapshot(
        Guid snapshotId,
        string graphId,
        string generationId,
        PackageGraphUseSnapshotState state,
        IEnumerable<PhysicalRootIdentity> roots,
        IEnumerable<PackageGraphRootSelection> requestedRoots,
        IEnumerable<PackageGraphNodeIdentity> nodes,
        IEnumerable<PackageGraphEdgeIdentity> edges)
    {
        if (snapshotId == Guid.Empty)
            throw new ArgumentException("A graph snapshot identifier cannot be empty.", nameof(snapshotId));
        ArgumentException.ThrowIfNullOrWhiteSpace(graphId);
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(requestedRoots);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        var rootArray = roots.ToArray();
        var requestedRootArray = requestedRoots.ToArray();
        var nodeArray = nodes.ToArray();
        var edgeArray = edges.ToArray();
        if (rootArray.Any(static root => root is null) ||
            requestedRootArray.Any(static root => root is null) ||
            nodeArray.Any(static node => node is null) ||
            edgeArray.Any(static edge => edge is null))
        {
            throw new ArgumentException("Graph snapshot collections cannot contain null items.");
        }

        SnapshotId = snapshotId;
        GraphId = graphId;
        GenerationId = generationId;
        State = state;
        Roots = new ReadOnlyCollection<PhysicalRootIdentity>(rootArray);
        RequestedRoots = new ReadOnlyCollection<PackageGraphRootSelection>(requestedRootArray);
        Nodes = new ReadOnlyCollection<PackageGraphNodeIdentity>(nodeArray);
        Edges = new ReadOnlyCollection<PackageGraphEdgeIdentity>(edgeArray);
    }

    /// <summary>Gets the immutable snapshot identifier.</summary>
    public Guid SnapshotId { get; }

    /// <summary>Gets the deterministic identity of this selected package graph.</summary>
    public string GraphId { get; }

    /// <summary>Gets the graph generation identifier.</summary>
    public string GenerationId { get; }

    /// <summary>Gets whether the graph state is pending or committed.</summary>
    public PackageGraphUseSnapshotState State { get; }

    /// <summary>Gets the physical roots represented by this graph.</summary>
    public IReadOnlyList<PhysicalRootIdentity> Roots { get; }

    /// <summary>Gets the original requested roots and the graph nodes selected to satisfy them.</summary>
    public IReadOnlyList<PackageGraphRootSelection> RequestedRoots { get; }

    /// <summary>Gets the copied selected package nodes.</summary>
    public IReadOnlyList<PackageGraphNodeIdentity> Nodes { get; }

    /// <summary>Gets the copied selected dependency edges.</summary>
    public IReadOnlyList<PackageGraphEdgeIdentity> Edges { get; }
}
