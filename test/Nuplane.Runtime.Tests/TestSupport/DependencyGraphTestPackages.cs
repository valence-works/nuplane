using Nuplane.Reconciliation.Models;

namespace Nuplane.Runtime.Tests.TestSupport;

internal static class DependencyGraphTestPackages
{
    public static ResolvedPackageNode Root(
        string packageId = "Plugin.Root",
        string version = "1.0.0",
        string sourceName = "test-feed") =>
        Node(packageId, version, PackageNodeRole.Root, sourceName);

    public static ResolvedPackageNode Dependency(
        string packageId = "Plugin.Dependency",
        string version = "1.0.0",
        string sourceName = "test-feed") =>
        Node(packageId, version, PackageNodeRole.Dependency, sourceName);

    public static DependencyEdge Edge(
        string fromPackageId = "Plugin.Root",
        string fromVersion = "1.0.0",
        string toPackageId = "Plugin.Dependency",
        string requestedVersionRange = "[1.0.0]",
        string selectedVersion = "1.0.0",
        string targetFramework = "net10.0") =>
        new(
            fromPackageId,
            fromVersion,
            toPackageId,
            requestedVersionRange,
            selectedVersion,
            targetFramework,
            Optional: false);

    public static ResolvedPackageGraph Graph(
        ResolvedPackageNode? root = null,
        ResolvedPackageNode? dependency = null,
        DependencyEdge? edge = null,
        string graphId = "graph-plugin-root",
        string generationId = "generation-1",
        string targetFramework = "net10.0")
    {
        var rootNode = root ?? Root();
        var dependencyNode = dependency ?? Dependency();
        return new(
            graphId,
            generationId,
            targetFramework,
            [rootNode],
            [rootNode, dependencyNode],
            [edge ?? Edge(rootNode.PackageId, rootNode.Version, dependencyNode.PackageId, $"[{dependencyNode.Version}]", dependencyNode.Version, targetFramework)],
            [],
            DateTimeOffset.UtcNow);
    }

    private static ResolvedPackageNode Node(
        string packageId,
        string version,
        PackageNodeRole role,
        string sourceName) =>
        new(
            packageId,
            version,
            role,
            InstallPath: null,
            PackageSourceKind.RemoteFeed,
            sourceName,
            PackageContentHash: null,
            RuntimeAssets: [],
            DiscoverableAssets: role is PackageNodeRole.Root or PackageNodeRole.RootAndDependency ? [$"{packageId}.dll"] : [],
            SupportAssets: role is PackageNodeRole.Dependency ? [$"{packageId}.dll"] : []);
}
