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
/// </remarks>
public interface IPackageTypeScanner
{
    /// <summary>
    /// Finds concrete, non-abstract types within the loaded package that implement <typeparamref name="TInterface"/>.
    /// </summary>
    /// <typeparam name="TInterface">The contract type to scan for.</typeparam>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="version">The package version.</param>
    /// <returns>The discovered matching types.</returns>
    IReadOnlyList<Type> FindTypes<TInterface>(string packageId, string version);

    /// <summary>
    /// Finds concrete, non-abstract types within the loaded package that implement the provided contract type.
    /// </summary>
    /// <param name="interfaceType">The interface or base type to scan for.</param>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="version">The package version.</param>
    /// <returns>The discovered matching types.</returns>
    IReadOnlyList<Type> FindTypes(Type interfaceType, string packageId, string version);
}