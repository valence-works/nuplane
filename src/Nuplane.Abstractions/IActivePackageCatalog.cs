namespace Nuplane.Abstractions;

/// <summary>
/// Standalone host-facing query service for the current active reconciled package inventory.
/// </summary>
public interface IActivePackageCatalog
{
    /// <summary>
    /// Reads the current active packages snapshot.
    /// </summary>
    Task<ActivePackagesSnapshot> GetActivePackagesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads the current active package catalog snapshot using legacy naming.
    /// </summary>
    /// <remarks>
    /// This member exists only as a transitional compatibility bridge inside the current codebase
    /// while the canonical host-facing vocabulary is moved to <see cref="GetActivePackagesAsync"/>.
    /// </remarks>
    async Task<ActivePackageCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        (await GetActivePackagesAsync(cancellationToken).ConfigureAwait(false)).ToLegacySnapshot();
}

