namespace Nuplane.Abstractions;

/// <summary>
/// Standalone host-facing query service for the current active reconciled package inventory.
/// </summary>
public interface IActivePackageCatalog
{
    /// <summary>
    /// Reads the current active package catalog snapshot.
    /// </summary>
    Task<ActivePackageCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

