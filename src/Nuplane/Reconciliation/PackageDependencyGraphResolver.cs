using System.Runtime.Versioning;
using System.Xml.Linq;
using Nuplane.Abstractions;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Reconciliation;

/// <summary>
/// Resolves the dependency closure for desired root packages from installed NuGet metadata.
/// </summary>
public sealed class PackageDependencyGraphResolver(IPackageResolver packageResolver, IReconciliationRetryPolicy retryPolicy)
{
    private readonly IPackageResolver packageResolver = packageResolver ?? throw new ArgumentNullException(nameof(packageResolver));
    private readonly IReconciliationRetryPolicy retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));

    /// <summary>
    /// Resolves desired roots and their package dependencies into deterministic graph records.
    /// </summary>
    /// <param name="desiredRequests">The desired root package requests.</param>
    /// <param name="resolveRootAsync">The callback used to resolve each root package.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The resolved packages and graph metadata.</returns>
    public async Task<PackageDependencyGraphResolutionResult> ResolveAsync(
        IReadOnlyList<PackageRequest> desiredRequests,
        Func<PackageRequest, CancellationToken, Task<ResolvedPackage>> resolveRootAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desiredRequests);
        ArgumentNullException.ThrowIfNull(resolveRootAsync);

        var resolvedPackages = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);
        var graphs = new List<ResolvedPackageGraph>();
        var generationId = Guid.NewGuid().ToString("N");

        foreach (var request in desiredRequests.OrderBy(static request => request.Id, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rootPackage = await resolveRootAsync(request, cancellationToken);
            resolvedPackages[BuildPackageKey(rootPackage.Id, rootPackage.Version)] = rootPackage;

            var rootNode = CreateNode(rootPackage, PackageNodeRole.Root, dependencyOfPackageIds: []);
            var nodes = new Dictionary<string, ResolvedPackageNode>(StringComparer.OrdinalIgnoreCase)
            {
                [BuildPackageKey(rootPackage.Id, rootPackage.Version)] = rootNode
            };
            var edges = new List<DependencyEdge>();
            var queue = new Queue<(ResolvedPackage Parent, PackageDependencyMetadata Dependency)>();

            foreach (var dependency in ReadDependencyMetadata(rootPackage))
            {
                queue.Enqueue((rootPackage, dependency));
            }

            while (queue.Count > 0)
            {
                var (parent, dependency) = queue.Dequeue();
                var dependencyRequest = new PackageRequest(
                    dependency.PackageId,
                    dependency.VersionRange,
                    request.FeedName,
                    PackageUpdatePolicy.Exact,
                    request.SourceName);

                var dependencyPackage = await retryPolicy.ExecuteAsync(
                    ct => packageResolver.ResolveAsync(dependencyRequest, ct),
                    cancellationToken);
                var dependencyKey = BuildPackageKey(dependencyPackage.Id, dependencyPackage.Version);
                resolvedPackages[dependencyKey] = dependencyPackage;

                edges.Add(new DependencyEdge(
                    parent.Id,
                    parent.Version,
                    dependencyPackage.Id,
                    dependency.VersionRange,
                    dependencyPackage.Version,
                    dependency.TargetFramework ?? string.Empty,
                    Optional: false));

                if (nodes.ContainsKey(dependencyKey))
                {
                    continue;
                }

                nodes[dependencyKey] = CreateNode(dependencyPackage, PackageNodeRole.Dependency, [parent.Id]);

                foreach (var transitiveDependency in ReadDependencyMetadata(dependencyPackage))
                {
                    queue.Enqueue((dependencyPackage, transitiveDependency));
                }
            }

            var orderedNodes = nodes.Values
                .OrderBy(static node => node.PackageId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static node => node.Version, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var orderedEdges = edges
                .OrderBy(static edge => edge.FromPackageId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static edge => edge.ToPackageId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static edge => edge.SelectedVersion, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var graphId = ResolvedPackageGraph.CreateGraphId(
                TargetFrameworkMonikerProvider.Current,
                [rootNode],
                orderedNodes,
                orderedEdges,
                []);

            graphs.Add(new ResolvedPackageGraph(
                graphId,
                generationId,
                TargetFrameworkMonikerProvider.Current,
                [rootNode],
                orderedNodes,
                orderedEdges,
                [],
                DateTimeOffset.UtcNow));
        }

        return new PackageDependencyGraphResolutionResult(
            resolvedPackages.Values
                .OrderBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static package => package.Version, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            graphs);
    }

    private static ResolvedPackageNode CreateNode(
        ResolvedPackage package,
        PackageNodeRole role,
        IReadOnlyList<string> dependencyOfPackageIds) =>
        new(
            package.Id,
            package.Version,
            role,
            package.InstallPath,
            PackageSourceKind.RemoteFeed,
            string.IsNullOrWhiteSpace(package.SourceName) ? package.FeedName : package.SourceName,
            PackageContentHash: null,
            RuntimeAssets: ResolveRuntimeAssets(package.InstallPath),
            DiscoverableAssets: role is PackageNodeRole.Root or PackageNodeRole.RootAndDependency ? ResolveRuntimeAssets(package.InstallPath) : [],
            SupportAssets: role is PackageNodeRole.Dependency ? ResolveRuntimeAssets(package.InstallPath) : []);

    private static IReadOnlyList<string> ResolveRuntimeAssets(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(installPath, "*.dll", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(installPath, path))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<PackageDependencyMetadata> ReadDependencyMetadata(ResolvedPackage package)
    {
        if (string.IsNullOrWhiteSpace(package.InstallPath) || !Directory.Exists(package.InstallPath))
        {
            return [];
        }

        var nuspecPath = Directory
            .EnumerateFiles(package.InstallPath, "*.nuspec", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (nuspecPath is null)
        {
            return [];
        }

        using var stream = File.OpenRead(nuspecPath);
        var document = XDocument.Load(stream);

        return document
            .Descendants()
            .Where(static element => element.Name.LocalName == "dependency")
            .Select(static element => new PackageDependencyMetadata(
                element.Attribute("id")?.Value ?? string.Empty,
                element.Attribute("version")?.Value ?? string.Empty,
                element.Parent?.Name.LocalName == "group" ? element.Parent.Attribute("targetFramework")?.Value : null))
            .Where(static dependency => !string.IsNullOrWhiteSpace(dependency.PackageId)
                && !string.IsNullOrWhiteSpace(dependency.VersionRange))
            .OrderBy(static dependency => dependency.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildPackageKey(string packageId, string version) => $"{packageId}@{version}";

    private sealed record PackageDependencyMetadata(string PackageId, string VersionRange, string? TargetFramework);
}

/// <summary>
/// Contains dependency-graph resolution output for a reconciliation cycle.
/// </summary>
/// <param name="ResolvedPackages">All resolved graph packages.</param>
/// <param name="ResolvedGraphs">The resolved dependency graphs.</param>
public sealed record PackageDependencyGraphResolutionResult(
    IReadOnlyList<ResolvedPackage> ResolvedPackages,
    IReadOnlyList<ResolvedPackageGraph> ResolvedGraphs);

internal static class TargetFrameworkMonikerProvider
{
    public static string Current { get; } = typeof(PackageDependencyGraphResolver).Assembly
        .GetCustomAttributes(typeof(TargetFrameworkAttribute), inherit: false)
        .OfType<TargetFrameworkAttribute>()
        .FirstOrDefault()
        ?.FrameworkName ?? ".NETCoreApp";
}
