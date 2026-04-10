using System.Reflection;
using Nuplane.Loading;
using Nuplane.Sample.Abstractions;

namespace Nuplane.Sample.AspNetCore.Catalog;

/// <summary>
/// Sample-only query service that explicitly discovers plugin types from the current active package set,
/// layered on top of the assembly catalog convenience surface.
/// </summary>
internal sealed class PluginCatalog(
    IPackageAssemblyCatalog packageAssemblyCatalog,
    ILogger<PluginCatalog> logger)
{
    private readonly IPackageAssemblyCatalog _packageAssemblyCatalog = packageAssemblyCatalog ?? throw new ArgumentNullException(nameof(packageAssemblyCatalog));
    private readonly ILogger<PluginCatalog> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Discovers all currently scanable <see cref="IPlugin"/> implementations from active loaded packages.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredPluginDescriptor>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var discovered = new List<DiscoveredPluginDescriptor>();

        foreach (var package in (await _packageAssemblyCatalog.GetAssembliesAsync(cancellationToken))
                     .Where(static package => package.ScanCandidates.Count > 0))
        {
            var pluginTypes = package.Assemblies
                .SelectMany(assembly => GetCandidateTypes(assembly, package.PackageId, package.Version))
                .Where(static pluginType => pluginType is { IsAbstract: false, IsInterface: false } && typeof(IPlugin).IsAssignableFrom(pluginType))
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

    private IReadOnlyList<Type> GetCandidateTypes(Assembly assembly, string packageId, string version)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            _logger.LogWarning(
                ex,
                "Partially scanned assembly {AssemblyName} for sample plugin discovery from package {PackageId}@{Version}; {LoaderExceptionCount} exported types could not be loaded.",
                assembly.FullName ?? assembly.GetName().Name ?? "<unknown>",
                packageId,
                version,
                ex.LoaderExceptions.Length);

            return ex.Types.Where(type => type is not null).Cast<Type>().ToArray();
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException or BadImageFormatException)
        {
            _logger.LogWarning(
                ex,
                "Skipping assembly {AssemblyName} during sample plugin discovery for package {PackageId}@{Version} because exported types could not be inspected.",
                assembly.FullName ?? assembly.GetName().Name ?? "<unknown>",
                packageId,
                version);

            return [];
        }
    }
}

internal sealed record DiscoveredPluginDescriptor(
    string PackageId,
    string Version,
    string PluginType,
    string AssemblyName,
    IReadOnlyList<string> ScanCandidateAssemblyFileNames);

