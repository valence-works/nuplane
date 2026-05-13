using System.Runtime.Versioning;
using System.Text.Json;
using System.Xml.Linq;
using Nuplane.Abstractions;
using Nuplane.Reconciliation.Models;
using NuGet.Versioning;

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

            var rootNode = CreateNode(rootPackage, PackageNodeRole.Root);
            var nodes = new Dictionary<string, ResolvedPackageNode>(StringComparer.OrdinalIgnoreCase)
            {
                [BuildPackageKey(rootPackage.Id, rootPackage.Version)] = rootNode
            };
            var edges = new List<DependencyEdge>();
            var queue = new Queue<(ResolvedPackage Parent, PackageDependencyMetadata Dependency, IReadOnlyList<string> Path)>();
            var rootKey = BuildPackageKey(rootPackage.Id, rootPackage.Version);

            foreach (var dependency in ReadDependencyMetadata(rootPackage))
            {
                queue.Enqueue((rootPackage, dependency, [rootKey]));
            }

            while (queue.Count > 0)
            {
                var (parent, dependency, path) = queue.Dequeue();
                if (IsHostProvidedDependency(dependency.PackageId, dependency.VersionRange))
                {
                    continue;
                }

                var dependencyRequest = new PackageRequest(
                    dependency.PackageId,
                    dependency.VersionRange,
                    FeedName: null,
                    PackageUpdatePolicy.Exact,
                    $"dependency-of:{parent.Id}");

                var dependencyPackage = await retryPolicy.ExecuteAsync(
                    ct => packageResolver.ResolveAsync(dependencyRequest, ct),
                    cancellationToken);
                var dependencyKey = BuildPackageKey(dependencyPackage.Id, dependencyPackage.Version);
                resolvedPackages[dependencyKey] = dependencyPackage;

                if (path.Contains(dependencyKey, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Dependency cycle detected: {FormatCyclePath(path, dependencyKey)}.");
                }

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

                nodes[dependencyKey] = CreateNode(dependencyPackage, PackageNodeRole.Dependency);

                foreach (var transitiveDependency in ReadDependencyMetadata(dependencyPackage))
                {
                    queue.Enqueue((dependencyPackage, transitiveDependency, path.Concat([dependencyKey]).ToArray()));
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
        PackageNodeRole role) =>
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

        var groups = document
            .Descendants()
            .Where(static element => element.Name.LocalName == "group" && element.Parent?.Name.LocalName == "dependencies")
            .Select(static group => new DependencyGroupMetadata(
                group.Attribute("targetFramework")?.Value,
                group.Elements()
                    .Where(static element => element.Name.LocalName == "dependency")
                    .Select(element => new PackageDependencyMetadata(
                        element.Attribute("id")?.Value ?? string.Empty,
                        NormalizeDependencyVersionRange(element.Attribute("version")?.Value ?? string.Empty),
                        group.Attribute("targetFramework")?.Value))
                    .ToArray()))
            .ToArray();

        var selectedGroups = SelectDependencyGroups(groups);
        if (selectedGroups.Count > 0)
        {
            return selectedGroups
                .SelectMany(static group => group.Dependencies)
                .Where(static dependency => !string.IsNullOrWhiteSpace(dependency.PackageId)
                    && !string.IsNullOrWhiteSpace(dependency.VersionRange))
                .OrderBy(static dependency => dependency.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return document
            .Descendants()
            .Where(static element => element.Name.LocalName == "dependency" && element.Parent?.Name.LocalName == "dependencies")
            .Select(static element => new PackageDependencyMetadata(
                element.Attribute("id")?.Value ?? string.Empty,
                NormalizeDependencyVersionRange(element.Attribute("version")?.Value ?? string.Empty),
                TargetFramework: null))
            .Where(static dependency => !string.IsNullOrWhiteSpace(dependency.PackageId)
                && !string.IsNullOrWhiteSpace(dependency.VersionRange))
            .OrderBy(static dependency => dependency.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeDependencyVersionRange(string versionRange)
    {
        if (string.IsNullOrWhiteSpace(versionRange))
        {
            return versionRange;
        }

        var trimmed = versionRange.Trim();
        if (trimmed.StartsWith('[') || trimmed.StartsWith('('))
        {
            return trimmed;
        }

        return NuGetVersion.TryParse(trimmed, out var version)
            ? $"[{version.ToNormalizedString()},)"
            : trimmed;
    }

    private static IReadOnlyList<DependencyGroupMetadata> SelectDependencyGroups(IReadOnlyList<DependencyGroupMetadata> groups)
    {
        if (groups.Count == 0)
        {
            return [];
        }

        if (!TryParseFramework(TargetFrameworkMonikerProvider.Current, out var hostTarget))
        {
            return groups.Where(static group => string.IsNullOrWhiteSpace(group.TargetFramework)).ToArray();
        }

        var compatibleGroups = groups
            .Select(group => new
            {
                Group = group,
                Parsed = TryParseFramework(group.TargetFramework, out var target) ? target : null
            })
            .Where(candidate => candidate.Parsed is not null && IsCompatible(hostTarget, candidate.Parsed))
            .OrderByDescending(candidate => candidate.Parsed!.Kind == hostTarget.Kind ? 1 : 0)
            .ThenByDescending(candidate => candidate.Parsed!.Version)
            .Select(candidate => candidate.Group)
            .ToArray();

        if (compatibleGroups.Length > 0)
        {
            return [compatibleGroups[0]];
        }

        return groups.Where(static group => string.IsNullOrWhiteSpace(group.TargetFramework)).ToArray();
    }

    private static bool IsCompatible(FrameworkTarget host, FrameworkTarget candidate)
    {
        if (candidate.Kind == FrameworkKind.NetStandard)
        {
            return candidate.Version <= new Version(2, 1);
        }

        return candidate.Kind == host.Kind && candidate.Version <= host.Version;
    }

    private static bool TryParseFramework(string? value, out FrameworkTarget target)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            target = null!;
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith(".NETCoreApp,Version=v", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseVersion(normalized[21..], FrameworkKind.NetCoreApp, out target);
        }

        if (normalized.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseVersion(normalized[11..], FrameworkKind.NetStandard, out target);
        }

        if (normalized.StartsWith("net", StringComparison.OrdinalIgnoreCase) && normalized.Contains('.'))
        {
            return TryParseVersion(normalized[3..], FrameworkKind.NetCoreApp, out target);
        }

        target = null!;
        return false;
    }

    private static bool TryParseVersion(string value, FrameworkKind kind, out FrameworkTarget target)
    {
        if (Version.TryParse(value, out var version))
        {
            target = new FrameworkTarget(kind, new Version(version.Major, version.Minor));
            return true;
        }

        target = null!;
        return false;
    }

    private static string BuildPackageKey(string packageId, string version) => $"{packageId}@{version}";

    private static string FormatCyclePath(IReadOnlyList<string> path, string repeatedKey)
    {
        var cycleStartIndex = path
            .Select((key, index) => new { key, index })
            .First(item => string.Equals(item.key, repeatedKey, StringComparison.OrdinalIgnoreCase))
            .index;

        return string.Join(" -> ", path.Skip(cycleStartIndex).Concat([repeatedKey]));
    }

    private static readonly Lazy<IReadOnlyDictionary<string, string>> HostPackageVersions = new(LoadHostPackageVersions);

    private static bool IsHostProvidedDependency(string packageId, string versionRange)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        if (IsSharedHostContractPackage(packageId))
        {
            return true;
        }

        return HostPackageVersions.Value.TryGetValue(packageId, out var hostVersion) &&
            VersionSatisfiesRange(hostVersion, versionRange);
    }

    private static bool IsSharedHostContractPackage(string packageId) =>
        packageId.Equals("CShells.Abstractions", StringComparison.OrdinalIgnoreCase) ||
        packageId.Equals("CShells.AspNetCore.Abstractions", StringComparison.OrdinalIgnoreCase) ||
        packageId.Equals("CShells.FastEndpoints.Abstractions", StringComparison.OrdinalIgnoreCase) ||
        packageId.Equals("Nuplane.Abstractions", StringComparison.OrdinalIgnoreCase) ||
        packageId.Equals("Nuplane.Loading.Abstractions", StringComparison.OrdinalIgnoreCase) ||
        packageId.Equals("Elsa.Api.Common", StringComparison.OrdinalIgnoreCase) ||
        packageId.Equals("Elsa.Caching", StringComparison.OrdinalIgnoreCase) ||
        packageId.Equals("Elsa.Common", StringComparison.OrdinalIgnoreCase) ||
        packageId.Equals("Elsa.Expressions", StringComparison.OrdinalIgnoreCase) ||
        packageId.Equals("Elsa.Features", StringComparison.OrdinalIgnoreCase) ||
        packageId.Equals("Elsa.KeyValues", StringComparison.OrdinalIgnoreCase) ||
        packageId.Equals("Elsa.Mediator", StringComparison.OrdinalIgnoreCase) ||
        packageId.Equals("Elsa.Resilience", StringComparison.OrdinalIgnoreCase) ||
        packageId.Equals("Elsa.Resilience.Core", StringComparison.OrdinalIgnoreCase) ||
        packageId.Equals("Elsa.Tenants", StringComparison.OrdinalIgnoreCase) ||
        packageId.Equals("Elsa.Workflows.Core", StringComparison.OrdinalIgnoreCase) ||
        packageId.Equals("Elsa.Workflows.Management", StringComparison.OrdinalIgnoreCase) ||
        packageId.Equals("Elsa.Workflows.Runtime", StringComparison.OrdinalIgnoreCase) ||
        packageId.StartsWith("Microsoft.Extensions.", StringComparison.OrdinalIgnoreCase);

    private static bool VersionSatisfiesRange(string hostVersion, string versionRange)
    {
        if (!NuGetVersion.TryParse(hostVersion, out var parsedHostVersion))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(versionRange))
        {
            return false;
        }

        if (NuGetVersion.TryParse(versionRange, out _) &&
            !versionRange.StartsWith('[') &&
            !versionRange.StartsWith('('))
        {
            versionRange = $"[{versionRange}]";
        }

        return VersionRange.TryParse(versionRange, out var range) && range.Satisfies(parsedHostVersion);
    }

    private static IReadOnlyDictionary<string, string> LoadHostPackageVersions()
    {
        var packageVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var depsFile in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.deps.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(depsFile));
            if (!document.RootElement.TryGetProperty("libraries", out var libraries) ||
                libraries.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var library in libraries.EnumerateObject())
            {
                var separatorIndex = library.Name.LastIndexOf('/');
                if (separatorIndex <= 0 || separatorIndex == library.Name.Length - 1)
                {
                    continue;
                }

                var packageId = library.Name[..separatorIndex];
                var version = library.Name[(separatorIndex + 1)..];
                if (!NuGetVersion.TryParse(version, out var parsedVersion))
                {
                    continue;
                }

                if (!packageVersions.TryGetValue(packageId, out var existingVersion) ||
                    !NuGetVersion.TryParse(existingVersion, out var parsedExistingVersion) ||
                    parsedVersion > parsedExistingVersion)
                {
                    packageVersions[packageId] = parsedVersion.ToNormalizedString();
                }
            }
        }

        return packageVersions;
    }

    private sealed record DependencyGroupMetadata(string? TargetFramework, IReadOnlyList<PackageDependencyMetadata> Dependencies);

    private sealed record PackageDependencyMetadata(string PackageId, string VersionRange, string? TargetFramework);

    private sealed record FrameworkTarget(FrameworkKind Kind, Version Version);

    private enum FrameworkKind
    {
        NetCoreApp,
        NetStandard
    }
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
