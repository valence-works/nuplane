using System.Reflection;

namespace Nuplane.Loading;

/// <summary>
/// Provides package-scoped access to assemblies loaded into package load contexts.
/// </summary>
/// <remarks>
/// <para>
/// Higher-level host integrations should usually prefer <see cref="IPackageAssemblyCatalog"/>, which applies
/// active-package and loading-state defaults before materializing assemblies.
/// </para>
/// <para>
/// The <see cref="Assembly"/> instances returned by methods on this interface originate from assemblies
/// loaded into collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/> instances.
/// Holding references to these assemblies (or to derived reflection artifacts such as
/// <see cref="Type"/>, <see cref="System.Reflection.MethodInfo"/>, <see cref="System.Reflection.PropertyInfo"/>, etc.)
/// will prevent the owning load context from being garbage collected and unloaded.
/// </para>
/// <para>
/// Callers should inspect returned assemblies immediately and avoid caching them beyond the current
/// reconciliation cycle to preserve runtime unload behavior.
/// </para>
/// </remarks>
internal interface IPackageAssemblyProvider
{
    /// <summary>
    /// Gets the assemblies Nuplane has loaded (or can deterministically materialize) for the specified package version.
    /// </summary>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="version">The package version.</param>
    /// <returns>
    /// The loaded package assemblies. Assembly materialization is best-effort: candidate assemblies that cannot be loaded
    /// because files are missing, invalid, or otherwise unavailable are skipped, and any remaining resolvable assemblies are returned.
    /// </returns>
    IReadOnlyList<Assembly> GetAssemblies(string packageId, string version);
}

