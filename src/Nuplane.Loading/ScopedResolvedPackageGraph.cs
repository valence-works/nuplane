using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Loading;

/// <summary>Copies one actual resolved graph and its package projection while taking a published lease owner.</summary>
internal sealed class ScopedResolvedPackageGraph
{
    internal ScopedResolvedPackageGraph(
        ResolvedPackageGraph graph,
        IReadOnlyList<ResolvedPackage> packages,
        IReadOnlyList<PackageRequest> originalRootRequests,
        PackageGraphUseLeaseOwner leaseOwner)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(originalRootRequests);
        ArgumentNullException.ThrowIfNull(leaseOwner);

        Graph = CopyGraph(graph);
        Packages = Array.AsReadOnly(packages.Select(static package =>
        {
            ArgumentNullException.ThrowIfNull(package);
            return package with { };
        }).ToArray());
        OriginalRootRequests = Array.AsReadOnly(originalRootRequests.Select(static request =>
        {
            ArgumentNullException.ThrowIfNull(request);
            return request with { };
        }).ToArray());
        LeaseOwner = leaseOwner;
    }

    internal ResolvedPackageGraph Graph { get; }
    internal IReadOnlyList<ResolvedPackage> Packages { get; }
    internal IReadOnlyList<PackageRequest> OriginalRootRequests { get; }
    internal PackageGraphUseLeaseOwner LeaseOwner { get; }

    private static ResolvedPackageGraph CopyGraph(ResolvedPackageGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph.Roots);
        ArgumentNullException.ThrowIfNull(graph.Nodes);
        ArgumentNullException.ThrowIfNull(graph.Edges);
        ArgumentNullException.ThrowIfNull(graph.SourceDecisions);
        return graph with
        {
            Roots = Array.AsReadOnly(graph.Roots.Select(CopyNode).ToArray()),
            Nodes = Array.AsReadOnly(graph.Nodes.Select(CopyNode).ToArray()),
            Edges = Array.AsReadOnly(graph.Edges.Select(static edge => edge with { }).ToArray()),
            SourceDecisions = Array.AsReadOnly(graph.SourceDecisions.Select(static decision => decision with
            {
                CandidateFeeds = Array.AsReadOnly(decision.CandidateFeeds.ToArray())
            }).ToArray())
        };
    }

    private static ResolvedPackageNode CopyNode(ResolvedPackageNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node with
        {
            RuntimeAssets = Array.AsReadOnly(node.RuntimeAssets.ToArray()),
            DiscoverableAssets = Array.AsReadOnly(node.DiscoverableAssets.ToArray()),
            SupportAssets = Array.AsReadOnly(node.SupportAssets.ToArray())
        };
    }
}
