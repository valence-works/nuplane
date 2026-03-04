using Nuplane.Abstractions;
using Nuplane.Loading;
using Nuplane.Loading.Configuration;
using Nuplane.Runtime.Loading;

namespace Nuplane.Loading.Hosting;

/// <summary>
/// Adapts the concrete <see cref="IPackageLoader"/> from the Loading SDK
/// to the runtime-level <see cref="IPackageLoaderBoundary"/> contract.
/// When loading is enabled, delegates to the underlying loader and maps results
/// to per-package <see cref="PackageLoaderOutcome"/> entries. When disabled,
/// all packages receive <see cref="PackageLoaderOutcome.Skipped"/> outcomes.
/// </summary>
public sealed class NuplaneLoadingAdapter : IPackageLoaderBoundary
{
    private readonly LoadingOptions _loadingOptions;
    private readonly IPackageLoader _packageLoader;

    /// <summary>
    /// Initializes a new instance of the <see cref="NuplaneLoadingAdapter"/> class.
    /// </summary>
    /// <param name="loadingOptions">The loading configuration options.</param>
    /// <param name="packageLoader">The underlying package loader implementation.</param>
    public NuplaneLoadingAdapter(LoadingOptions loadingOptions, IPackageLoader packageLoader)
    {
        _loadingOptions = loadingOptions ?? throw new ArgumentNullException(nameof(loadingOptions));
        _packageLoader = packageLoader ?? throw new ArgumentNullException(nameof(packageLoader));
    }

    /// <inheritdoc />
    public async Task<PackageLoaderBoundaryResult> LoadAsync(
        IReadOnlyList<ResolvedPackage> packages,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        if (!_loadingOptions.Enabled || packages.Count == 0)
        {
            var skipped = packages.Select(p => new PackageLoaderBoundaryEntry(
                p.Id, p.Version, PackageLoaderOutcome.Skipped, "loader-disabled")).ToList();
            return new PackageLoaderBoundaryResult(skipped);
        }

        var sharedPolicy = _loadingOptions.SharedAssemblies
            .Select(x => new SharedAssemblyPolicyEntry(x.Name, x.PublicKeyToken, x.MajorVersion))
            .ToArray();

        var loadResult = await _packageLoader.EnsureLoadedAsync(packages, sharedPolicy, cancellationToken);

        var entries = new List<PackageLoaderBoundaryEntry>(packages.Count);
        foreach (var package in packages)
        {
            if (loadResult.FailedByPackageId.TryGetValue(package.Id, out var reason))
            {
                entries.Add(new PackageLoaderBoundaryEntry(
                    package.Id, package.Version, PackageLoaderOutcome.Failed, reason));
            }
            else
            {
                entries.Add(new PackageLoaderBoundaryEntry(
                    package.Id, package.Version, PackageLoaderOutcome.Loaded, null));
            }
        }

        return new PackageLoaderBoundaryResult(entries);
    }
}
