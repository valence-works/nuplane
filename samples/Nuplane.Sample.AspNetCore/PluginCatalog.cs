using Nuplane.Loading;
using Nuplane.Sample.Abstractions;

namespace Nuplane.Sample.AspNetCore;

/// <summary>
/// Sample-only query service that explicitly discovers plugin types from the current active package set.
/// </summary>
internal sealed class PluginCatalog(
    ILoadingCatalog loadingCatalog,
    IPackageTypeScanner packageTypeScanner)
{
    private readonly ILoadingCatalog _loadingCatalog = loadingCatalog ?? throw new ArgumentNullException(nameof(loadingCatalog));
    private readonly IPackageTypeScanner _packageTypeScanner = packageTypeScanner ?? throw new ArgumentNullException(nameof(packageTypeScanner));

    /// <summary>
    /// Discovers all currently scanable <see cref="IPlugin"/> implementations from active loaded packages.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredPluginDescriptor>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _loadingCatalog.GetSnapshotAsync(cancellationToken);
        var discovered = new List<DiscoveredPluginDescriptor>();

        foreach (var package in snapshot.Packages
                     .Where(static package => package.Status == LoadingStatus.Loaded && package.ScanCandidates.Count > 0)
                     .OrderBy(static package => package.PackageId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static package => package.Version, StringComparer.OrdinalIgnoreCase))
        {
            var pluginTypes = _packageTypeScanner.FindTypes<IPlugin>(package.PackageId, package.Version)
                .OrderBy(static pluginType => pluginType.FullName, StringComparer.Ordinal)
                .ToArray();

            foreach (var pluginType in pluginTypes)
            {
                discovered.Add(new DiscoveredPluginDescriptor(
                    package.PackageId,
                    package.Version,
                    pluginType.FullName ?? pluginType.Name,
                    pluginType.Assembly.GetName().Name ?? pluginType.Assembly.FullName ?? "<unknown>",
                    package.ScanCandidates.Select(static candidate => candidate.AssemblyFileName).ToArray()));
            }
        }

        return discovered;
    }
}

internal sealed record DiscoveredPluginDescriptor(
    string PackageId,
    string Version,
    string PluginType,
    string AssemblyName,
    IReadOnlyList<string> ScanCandidateAssemblyFileNames);

