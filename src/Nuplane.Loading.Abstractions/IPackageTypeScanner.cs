namespace Nuplane.Loading;

/// <summary>
/// Provides package-scoped type discovery over assemblies loaded into package load contexts.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Type"/> instances returned by methods on this interface originate from assemblies
/// loaded into collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/> instances.
/// Holding references to these types (or derived reflection artifacts such as
/// <see cref="System.Reflection.MethodInfo"/>, <see cref="System.Reflection.PropertyInfo"/>, etc.)
/// will prevent the owning load context from being garbage collected and unloaded.
/// </para>
/// <para>
/// Callers should use returned types immediately (e.g., to create instances via
/// <see cref="Activator.CreateInstance(Type)"/>) and avoid caching them beyond the current
/// reconciliation cycle to preserve runtime unload behavior.
/// </para>
/// <para>
/// Hosts should generally start from <see cref="IPackageAssemblyCatalog"/> when they need package-aware assembly access.
/// <see cref="IPackageTypeScanner"/> is a convenience layer for cases where Nuplane should also perform assignability-based
/// type filtering over those assemblies.
/// </para>
/// </remarks>
public interface IPackageTypeScanner
{
    /// <summary>
    /// Finds concrete, non-abstract types within the current active loaded version of the specified package
    /// that implement <typeparamref name="TInterface"/>.
    /// </summary>
    /// <typeparam name="TInterface">The contract type to scan for.</typeparam>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The discovered matching types for the active loaded package version. Scanning is best-effort: assemblies
    /// or exported types that cannot be inspected because dependent assemblies are unavailable are skipped, and
    /// any remaining resolvable matches are returned. If the package is not active, not loaded, disabled, or stale,
    /// an empty result is returned.
    /// </returns>
    Task<IReadOnlyList<Type>> FindTypesAsync<TInterface>(string packageId, CancellationToken cancellationToken);

    /// <summary>
    /// Finds concrete, non-abstract types within the current active loaded version of the specified package
    /// that implement the provided contract type.
    /// </summary>
    /// <param name="interfaceType">The interface or base type to scan for.</param>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The discovered matching types for the active loaded package version. Scanning is best-effort: assemblies
    /// or exported types that cannot be inspected because dependent assemblies are unavailable are skipped, and
    /// any remaining resolvable matches are returned. If the package is not active, not loaded, disabled, or stale,
    /// an empty result is returned.
    /// </returns>
    Task<IReadOnlyList<Type>> FindTypesAsync(Type interfaceType, string packageId, CancellationToken cancellationToken);

    /// <summary>
    /// Finds concrete, non-abstract types within the loaded package that implement <typeparamref name="TInterface"/>.
    /// </summary>
    /// <typeparam name="TInterface">The contract type to scan for.</typeparam>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="version">The package version.</param>
    /// <returns>
    /// The discovered matching types. Scanning is best-effort: assemblies or exported types that cannot be inspected
    /// because dependent assemblies are unavailable are skipped, and any remaining resolvable matches are returned.
    /// </returns>
    IReadOnlyList<Type> FindTypes<TInterface>(string packageId, string version);

    /// <summary>
    /// Finds concrete, non-abstract types within the loaded package that implement the provided contract type.
    /// </summary>
    /// <param name="interfaceType">The interface or base type to scan for.</param>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="version">The package version.</param>
    /// <returns>
    /// The discovered matching types. Scanning is best-effort: assemblies or exported types that cannot be inspected
    /// because dependent assemblies are unavailable are skipped, and any remaining resolvable matches are returned.
    /// </returns>
    IReadOnlyList<Type> FindTypes(Type interfaceType, string packageId, string version);
}