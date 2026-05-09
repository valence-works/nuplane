using System.Reflection;

namespace Nuplane.Loading;

/// <summary>
/// Provides a convenience query surface for reading assemblies from the current active loaded package set.
/// </summary>
/// <remarks>
/// <para>
/// This catalog applies sane defaults for the common host scenario: it returns only packages that are both
/// active and currently <see cref="PackageLoadStatus.Loaded"/> in the current process.
/// </para>
/// <para>
/// When loading is disabled, stale, or otherwise unavailable for the current process, this catalog returns
/// an empty result. Callers that need detailed availability reasoning should query <see cref="IPackageLoadStateCatalog"/>
/// directly before or alongside this convenience surface.
/// </para>
/// <para>
/// The <see cref="Assembly"/> instances returned by this interface originate from assemblies loaded into
/// collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/> instances. Holding references to these
/// assemblies beyond the current reconciliation cycle will prevent the owning load context from being garbage
/// collected and unloaded.
/// </para>
/// </remarks>
public interface IPackageAssemblyCatalog
{
    /// <summary>
    /// Gets assemblies for the current active loaded package set using loading-owned default filtering.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// A deterministic, package-grouped list of loaded assemblies and their corresponding durable assembly references.
    /// </returns>
    Task<IReadOnlyList<PackageAssemblies>> GetPackagedAssembliesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets assemblies for the current active loaded version of a specific package using loading-owned default filtering.
    /// </summary>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The active loaded package entry for the specified package identifier when one exists;
    /// otherwise <see langword="null"/>.
    /// </returns>
    Task<PackageAssemblies?> GetPackagedAssembliesAsync(string packageId, CancellationToken cancellationToken);
}

/// <summary>
/// Represents the loaded assemblies Nuplane currently exposes for one active package.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="Version">The active package version.</param>
/// <param name="Assemblies">The loaded assemblies materialized for the package.</param>
/// <param name="AssemblyReferences">The deterministic durable assembly references associated with the package.</param>
/// <param name="LoadMode">The effective load mode used for the package.</param>
/// <param name="FrameworkIntegrationSafe">Whether the loaded assemblies are safe for framework integration.</param>
public sealed record PackageAssemblies(
    string PackageId,
    string Version,
    IReadOnlyList<Assembly> Assemblies,
    IReadOnlyList<PackageAssemblyReference> AssemblyReferences,
    PackageLoadMode LoadMode = PackageLoadMode.Collectible,
    bool FrameworkIntegrationSafe = false);

/// <summary>
/// Legacy assembly catalog entry retained temporarily while the repo migrates to <see cref="PackageAssemblies"/>.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="Version">The active package version.</param>
/// <param name="Assemblies">The loaded assemblies materialized for the package.</param>
/// <param name="ScanCandidates">The deterministic scan candidates associated with the package.</param>
/// <param name="LoadMode">The effective load mode used for the package.</param>
/// <param name="FrameworkIntegrationSafe">Whether the loaded assemblies are safe for framework integration.</param>
internal sealed record PackageAssemblyCatalogEntry(
    string PackageId,
    string Version,
    IReadOnlyList<Assembly> Assemblies,
    IReadOnlyList<AssemblyScanCandidate> ScanCandidates,
    PackageLoadMode LoadMode = PackageLoadMode.Collectible,
    bool FrameworkIntegrationSafe = false)
{
    /// <summary>
    /// Converts this legacy assembly catalog entry to the canonical <see cref="PackageAssemblies"/> model.
    /// </summary>
    public PackageAssemblies ToPackageAssemblies() =>
        new(
            PackageId,
            Version,
            Assemblies,
            ScanCandidates.Select(static candidate => PackageAssemblyReference.FromCandidate(candidate)).ToArray(),
            LoadMode,
            FrameworkIntegrationSafe);

    /// <summary>
    /// Creates a legacy assembly catalog entry from the canonical <see cref="PackageAssemblies"/> model.
    /// </summary>
    public static PackageAssemblyCatalogEntry FromPackageAssemblies(PackageAssemblies package) =>
        new(
            package.PackageId,
            package.Version,
            package.Assemblies,
            package.AssemblyReferences.Select(static reference => reference.ToCandidate()).ToArray(),
            package.LoadMode,
            package.FrameworkIntegrationSafe);
}
