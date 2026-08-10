using Nuplane.Abstractions;

namespace Nuplane.Loading;

/// <summary>
/// Manages the loading of package assemblies into isolated assembly load contexts.
/// </summary>
internal interface IPackageLoader
{
    /// <summary>
    /// Ensures that all specified packages are loaded into assembly contexts, returning the load results.
    /// </summary>
    /// <param name="packages">The resolved packages to load.</param>
    /// <param name="sharedPolicy">The shared assembly policy entries controlling host assembly sharing.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result containing loaded sessions and any failures.</returns>
    Task<PackageLoadResult> EnsureLoadedAsync(
        IReadOnlyList<ResolvedPackage> packages,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken);

    /// <summary>
    /// Ensures that each supplied package graph is loaded into its own assembly context.
    /// </summary>
    /// <param name="packageGraphs">The graph-scoped package groups to load.</param>
    /// <param name="sharedPolicy">The shared assembly policy entries controlling host assembly sharing.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result containing loaded sessions and any failures.</returns>
    Task<PackageLoadResult> EnsureGraphLoadedAsync(
        IReadOnlyList<IReadOnlyList<ResolvedPackage>> packageGraphs,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to remove the assembly load context for a specific package version.
    /// </summary>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="version">The package version.</param>
    /// <param name="context">When successful, receives the removed load context handle.</param>
    /// <returns><see langword="true"/> if the context was found and removed; otherwise <see langword="false"/>.</returns>
    bool TryRemoveContext(string packageId, string version, out PackageLoadContextHandle? context);

    /// <summary>
    /// Determines whether a package version was already evaluated by the loader and deliberately not loaded
    /// because it contributes no assemblies, either because it contains none (facade/native support packages)
    /// or because the host runtime already provides them.
    /// </summary>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="version">The package version.</param>
    /// <returns><see langword="true"/> if the package version is a known inert graph member; otherwise <see langword="false"/>.</returns>
    bool IsInertPackage(string packageId, string version);

    /// <summary>
    /// Attempts to get the active assembly load context for a specific package version without removing it.
    /// </summary>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="version">The package version.</param>
    /// <param name="context">When successful, receives the active load context handle.</param>
    /// <returns><see langword="true"/> if the context exists; otherwise <see langword="false"/>.</returns>
    bool TryGetContext(string packageId, string version, out PackageLoadContextHandle? context);
}
