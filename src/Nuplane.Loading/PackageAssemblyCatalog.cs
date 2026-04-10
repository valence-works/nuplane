namespace Nuplane.Loading;

/// <summary>
/// Default implementation of <see cref="IPackageAssemblyCatalog"/> that composes loading state with
/// package assembly materialization using loading-owned default filtering semantics.
/// </summary>
public sealed class PackageAssemblyCatalog(
    ILoadingCatalog loadingCatalog,
    IPackageAssemblyProvider packageAssemblyProvider) : IPackageAssemblyCatalog
{
    private readonly ILoadingCatalog _loadingCatalog = loadingCatalog ?? throw new ArgumentNullException(nameof(loadingCatalog));
    private readonly IPackageAssemblyProvider _packageAssemblyProvider = packageAssemblyProvider ?? throw new ArgumentNullException(nameof(packageAssemblyProvider));

    /// <inheritdoc />
    public async Task<IReadOnlyList<PackageAssemblyCatalogEntry>> GetAssembliesAsync(CancellationToken cancellationToken)
    {
        var packages = await GetLoadedPackagesAsync(cancellationToken);

        return packages
            .Select(CreateEntry)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<PackageAssemblyCatalogEntry?> GetAssembliesAsync(string packageId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        var packages = await GetLoadedPackagesAsync(cancellationToken);
        var matches = packages
            .Where(package => string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matches.Length switch
        {
            0 => null,
            1 => CreateEntry(matches[0]),
            _ => throw new InvalidOperationException($"Multiple active loaded package versions were found for '{packageId}'.")
        };
    }

    /// <inheritdoc />
    public async Task<PackageAssemblyCatalogEntry?> GetAssembliesAsync(string packageId, string version, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var packages = await GetLoadedPackagesAsync(cancellationToken);
        var package = packages.FirstOrDefault(package =>
            string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(package.Version, version, StringComparison.OrdinalIgnoreCase));

        return package is null
            ? null
            : CreateEntry(package);
    }

    private async Task<IReadOnlyList<LoadingPackageDescriptor>> GetLoadedPackagesAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _loadingCatalog.GetSnapshotAsync(cancellationToken);
        if (snapshot.Availability != LoadingCatalogAvailability.Available)
        {
            return [];
        }

        return snapshot.Packages
            .Where(static package => package.Status == LoadingStatus.Loaded)
            .OrderBy(static package => package.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static package => package.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private PackageAssemblyCatalogEntry CreateEntry(LoadingPackageDescriptor package) =>
        new(
            package.PackageId,
            package.Version,
            _packageAssemblyProvider.GetAssemblies(package.PackageId, package.Version),
            package.ScanCandidates.ToArray());
}
