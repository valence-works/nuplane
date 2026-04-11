namespace Nuplane.Loading;

/// <summary>
/// Optional convenience surface for finding matching runtime types from the current active loaded version of a package.
/// Hosts should prefer querying assemblies first through <see cref="IPackageAssemblyCatalog"/> and treat this contract as a secondary convenience.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Type"/> instances returned by this contract originate from assemblies loaded into collectible
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> instances. Treat them as immediate-use, no-cache values.
/// </para>
/// <para>
/// Results are best-effort: Nuplane skips uninspectable assemblies or types and continues scanning other runtime assemblies.
/// </para>
/// </remarks>
public interface IPackageTypeFinder
{
    /// <summary>
    /// Finds assignable types from the current active loaded version of the specified package.
    /// </summary>
    Task<IReadOnlyList<Type>> FindTypesAsync<TInterface>(string packageId, CancellationToken cancellationToken);

    /// <summary>
    /// Finds assignable types from the current active loaded version of the specified package.
    /// </summary>
    Task<IReadOnlyList<Type>> FindTypesAsync(Type interfaceType, string packageId, CancellationToken cancellationToken);
}

