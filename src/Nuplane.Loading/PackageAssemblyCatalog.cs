namespace Nuplane.Loading;

/// <summary>
/// Default implementation of <see cref="IPackageAssemblyCatalog"/> that composes loading state with
/// package assembly materialization using loading-owned default filtering semantics.
/// </summary>
internal sealed class PackageAssemblyCatalog(
    IPackageLoadStateCatalog loadStateCatalog,
    IPackageAssemblyProvider packageAssemblyProvider) : IPackageAssemblyCatalog
{
    private readonly IPackageLoadStateCatalog _loadStateCatalog = loadStateCatalog ?? throw new ArgumentNullException(nameof(loadStateCatalog));
    private readonly IPackageAssemblyProvider _packageAssemblyProvider = packageAssemblyProvider ?? throw new ArgumentNullException(nameof(packageAssemblyProvider));

    /// <inheritdoc />
    public async Task<IReadOnlyList<PackageAssemblies>> GetPackagedAssembliesAsync(CancellationToken cancellationToken)
    {
        var packages = await GetLoadedPackagesAsync(cancellationToken);

        return packages
            .Select(CreateEntry)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<PackageAssemblies?> GetPackagedAssembliesAsync(string packageId, CancellationToken cancellationToken)
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

    private async Task<IReadOnlyList<PackageLoadState>> GetLoadedPackagesAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _loadStateCatalog.GetLoadStateAsync(cancellationToken);
        if (snapshot.Availability != PackageLoadStateAvailability.Available)
        {
            return [];
        }

        return snapshot.Packages
            .Where(static package => package.Status == PackageLoadStatus.Loaded && package.Discoverable)
            .OrderBy(static package => package.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static package => package.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private PackageAssemblies CreateEntry(PackageLoadState package) =>
        new(
            package.PackageId,
            package.Version,
            _packageAssemblyProvider.GetAssemblies(package.PackageId, package.Version),
            package.AssemblyReferences.ToArray(),
            package.LoadMode,
            package.FrameworkIntegrationSafe);
}
