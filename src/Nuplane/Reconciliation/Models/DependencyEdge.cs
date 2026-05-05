namespace Nuplane.Reconciliation.Models;

/// <summary>
/// Represents a dependency relationship between two selected graph package nodes.
/// </summary>
/// <param name="FromPackageId">The package identifier declaring the dependency.</param>
/// <param name="FromVersion">The selected version declaring the dependency.</param>
/// <param name="ToPackageId">The dependency package identifier.</param>
/// <param name="RequestedVersionRange">The requested dependency version range.</param>
/// <param name="SelectedVersion">The selected dependency version.</param>
/// <param name="DependencyGroupTargetFramework">The target framework group that contributed the edge.</param>
/// <param name="Optional">Whether the dependency edge is optional.</param>
public sealed record DependencyEdge(
    string FromPackageId,
    string FromVersion,
    string ToPackageId,
    string RequestedVersionRange,
    string SelectedVersion,
    string DependencyGroupTargetFramework,
    bool Optional);
