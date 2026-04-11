using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Operational;

namespace Nuplane.Loading;

/// <summary>
/// Contributes loading-specific degraded reasons through the generic operational-state seam.
/// </summary>
internal sealed class LoadingOperationalStateContributor(
    IActivePackageCatalog activePackageCatalog,
    PackageLoader packageLoader,
    LoadingCatalogRefreshTracker refreshTracker,
    IOptions<LoadingOptions> options) : IOperationalStateContributor
{
    private readonly IActivePackageCatalog _activePackageCatalog = activePackageCatalog ?? throw new ArgumentNullException(nameof(activePackageCatalog));
    private readonly PackageLoader _packageLoader = packageLoader ?? throw new ArgumentNullException(nameof(packageLoader));
    private readonly LoadingCatalogRefreshTracker _refreshTracker = refreshTracker ?? throw new ArgumentNullException(nameof(refreshTracker));
    private readonly LoadingOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

    public async Task<OperationalStateContribution> ContributeAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return new OperationalStateContribution("loading", []);
        }

        var activeSnapshot = await _activePackageCatalog.GetActivePackagesAsync(cancellationToken);
        if (!_refreshTracker.HasRefreshed)
        {
            return new OperationalStateContribution(
                "loading",
                activeSnapshot.Packages.Count > 0 ? [$"load-state-stale:{activeSnapshot.Packages.Count}"] : []);
        }

        var issueCount = 0;
        var staleCount = 0;
        var divergenceCount = 0;

        foreach (var package in activeSnapshot.Packages)
        {
            var key = $"{package.PackageId}@{package.Version}";
            if (_packageLoader.Sessions.TryGetValue(key, out var session))
            {
                if (!session.IsLoaded)
                {
                    issueCount++;
                    divergenceCount++;
                }

                continue;
            }

            staleCount++;
        }

        var reasons = new List<string>(3);
        if (issueCount > 0)
        {
            reasons.Add($"load-state-issues:{issueCount}");
        }

        if (staleCount > 0)
        {
            reasons.Add($"load-state-stale:{staleCount}");
        }

        if (divergenceCount > 0)
        {
            reasons.Add($"load-state-divergence:{divergenceCount}");
        }

        return new OperationalStateContribution("loading", reasons);
    }
}

