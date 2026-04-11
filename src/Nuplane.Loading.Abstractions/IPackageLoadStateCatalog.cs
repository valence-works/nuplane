namespace Nuplane.Loading;

/// <summary>
/// Canonical host-facing query service for package load-state availability and per-package load state.
/// </summary>
public interface IPackageLoadStateCatalog
{
    /// <summary>
    /// Reads the current package load-state snapshot.
    /// </summary>
    Task<PackageLoadStateSnapshot> GetLoadStateAsync(CancellationToken cancellationToken);
}

