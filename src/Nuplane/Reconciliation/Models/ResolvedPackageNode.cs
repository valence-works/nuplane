namespace Nuplane.Reconciliation.Models;

/// <summary>
/// Represents a concrete package node selected for a resolved dependency graph.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="Version">The selected package version.</param>
/// <param name="Role">The package role within the graph.</param>
/// <param name="InstallPath">The install path for the selected package when installed.</param>
/// <param name="SourceKind">The selected source kind.</param>
/// <param name="SourceName">The feed or local source that selected the package.</param>
/// <param name="PackageContentHash">The selected package content hash when available.</param>
/// <param name="RuntimeAssets">The runtime assembly assets selected for the package.</param>
/// <param name="DiscoverableAssets">The runtime assembly assets exposed for root feature discovery.</param>
/// <param name="SupportAssets">The runtime assembly assets available only for binding/support.</param>
public sealed record ResolvedPackageNode(
    string PackageId,
    string Version,
    PackageNodeRole Role,
    string? InstallPath,
    PackageSourceKind SourceKind,
    string? SourceName,
    string? PackageContentHash,
    IReadOnlyList<string> RuntimeAssets,
    IReadOnlyList<string> DiscoverableAssets,
    IReadOnlyList<string> SupportAssets);

/// <summary>
/// Describes how a selected package participates in a resolved dependency graph.
/// </summary>
public enum PackageNodeRole
{
    /// <summary>
    /// The package is an explicit desired root.
    /// </summary>
    Root,

    /// <summary>
    /// The package was selected only because another package depends on it.
    /// </summary>
    Dependency,

    /// <summary>
    /// The package is both explicitly desired and selected as a dependency of another root.
    /// </summary>
    RootAndDependency
}

/// <summary>
/// Describes the package source kind that selected a graph node.
/// </summary>
public enum PackageSourceKind
{
    /// <summary>
    /// The package was selected from a configured NuGet feed.
    /// </summary>
    RemoteFeed,

    /// <summary>
    /// The package was selected from a configured local directory source.
    /// </summary>
    LocalDirectory
}
