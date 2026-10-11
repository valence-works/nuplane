using Nuplane.Abstractions;

namespace Nuplane.Reconciliation.Models;

/// <summary>Associates one actual resolved graph with its selecting roots and resolved package projection.</summary>
public sealed record ResolvedPackageGraphSelection
{
    /// <summary>Creates a graph selection with copied collection inputs.</summary>
    /// <param name="graph">The complete resolved graph.</param>
    /// <param name="rootRequests">The exact final requests that selected the graph roots.</param>
    /// <param name="packages">The resolved packages represented by this graph.</param>
    public ResolvedPackageGraphSelection(
        ResolvedPackageGraph graph,
        IReadOnlyList<PackageRequest> rootRequests,
        IReadOnlyList<ResolvedPackage> packages)
    {
        Graph = (graph ?? throw new ArgumentNullException(nameof(graph))).CreateImmutableSnapshot();
        RootRequests = Array.AsReadOnly((rootRequests ?? throw new ArgumentNullException(nameof(rootRequests))).ToArray());
        Packages = Array.AsReadOnly((packages ?? throw new ArgumentNullException(nameof(packages))).ToArray());
    }

    /// <summary>Gets the complete graph this selection refers to.</summary>
    public ResolvedPackageGraph Graph { get; }

    /// <summary>Gets the final root requests that selected every root in the graph.</summary>
    public IReadOnlyList<PackageRequest> RootRequests { get; }

    /// <summary>Gets the resolved package projection represented by this graph selection.</summary>
    public IReadOnlyList<ResolvedPackage> Packages { get; }
}
