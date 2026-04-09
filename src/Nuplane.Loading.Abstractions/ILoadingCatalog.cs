namespace Nuplane.Loading;

/// <summary>
/// Standalone host-facing query service for package loading state and scan guidance.
/// </summary>
public interface ILoadingCatalog
{
    /// <summary>
    /// Reads the current loading catalog snapshot.
    /// </summary>
    Task<LoadingCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

