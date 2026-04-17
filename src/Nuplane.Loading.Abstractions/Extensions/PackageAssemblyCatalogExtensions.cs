using System.Reflection;

namespace Nuplane.Loading;

/// <summary>
/// Extension methods for IPackageAssemblyCatalog to simplify common operations, such as flattening assembly collections.
/// </summary>
public static class PackageAssemblyCatalogExtensions
{
    /// <summary>
    /// Flattens the collection of packaged assemblies into a single enumerable of assemblies.
    /// </summary>
    /// <returns>A flattened enumerable of assemblies from all active loaded packages.</returns>
    public static async Task<IEnumerable<Assembly>> GetAssembliesAsync(this IPackageAssemblyCatalog catalog, CancellationToken cancellationToken)
    {
        var packagedAssemblies = await catalog.GetPackagedAssembliesAsync(cancellationToken);
        return packagedAssemblies.SelectMany(x => x.Assemblies).ToArray();
    }
}