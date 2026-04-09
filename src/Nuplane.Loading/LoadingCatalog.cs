using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Observability;

namespace Nuplane.Loading;

/// <summary>
/// Default runtime implementation of <see cref="ILoadingCatalog"/>.
/// Reports current-process loading state and deterministic scan guidance for the active package set.
/// </summary>
public sealed class LoadingCatalog(
    IActivePackageCatalog activePackageCatalog,
    PackageLoader packageLoader,
    AssemblyScanCandidateProjector candidateProjector,
    LoadingCatalogRefreshTracker refreshTracker,
    IOptions<LoadingOptions> options,
    IReconciliationLogger logger,
    ReconciliationMetrics metrics) : ILoadingCatalog
{
    private readonly IActivePackageCatalog _activePackageCatalog = activePackageCatalog ?? throw new ArgumentNullException(nameof(activePackageCatalog));
    private readonly PackageLoader _packageLoader = packageLoader ?? throw new ArgumentNullException(nameof(packageLoader));
    private readonly AssemblyScanCandidateProjector _candidateProjector = candidateProjector ?? throw new ArgumentNullException(nameof(candidateProjector));
    private readonly LoadingCatalogRefreshTracker _refreshTracker = refreshTracker ?? throw new ArgumentNullException(nameof(refreshTracker));
    private readonly LoadingOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly IReconciliationLogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ReconciliationMetrics _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));

    /// <inheritdoc />
    public async Task<LoadingCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var correlationId = CorrelationContext.CreateNew();
        var activeSnapshot = await _activePackageCatalog.GetSnapshotAsync(cancellationToken);

        if (!_options.Enabled)
        {
            var disabledPackages = activeSnapshot.Packages
                .Select(package => CreateDescriptor(package, LoadingStatus.Disabled, null, ["loading-disabled"], [], null))
                .ToArray();

            _logger.LogLoadingCatalogRead(correlationId, LoadingCatalogAvailability.Disabled.ToString(), disabledPackages.Length, "loading-disabled");
            _metrics.RecordLoadingCatalogRead(LoadingCatalogAvailability.Disabled.ToString(), disabledPackages.Length, degraded: false, reasonCode: "loading-disabled");

            return new LoadingCatalogSnapshot(
                LoadingCatalogAvailability.Disabled,
                DateTimeOffset.UtcNow,
                _refreshTracker.RefreshedAtUtc,
                disabledPackages,
                "loading-disabled",
                correlationId);
        }

        if (!_refreshTracker.HasRefreshed)
        {
            var stalePackages = activeSnapshot.Packages
                .Select(package => CreateDescriptor(package, LoadingStatus.Stale, null, ["loading-not-refreshed-for-current-process"], [], null))
                .ToArray();

            _logger.LogLoadingCatalogRead(correlationId, LoadingCatalogAvailability.Stale.ToString(), stalePackages.Length, "loading-stale");
            _metrics.RecordLoadingCatalogRead(LoadingCatalogAvailability.Stale.ToString(), stalePackages.Length, degraded: stalePackages.Length > 0, reasonCode: "loading-stale");

            return new LoadingCatalogSnapshot(
                LoadingCatalogAvailability.Stale,
                DateTimeOffset.UtcNow,
                null,
                stalePackages,
                "loading-stale",
                correlationId);
        }

        var descriptors = new List<LoadingPackageDescriptor>(activeSnapshot.Packages.Count);
        var issueCount = 0;
        var staleCount = 0;
        var divergenceCount = 0;

        foreach (var package in activeSnapshot.Packages)
        {
            var key = $"{package.PackageId}@{package.Version}";
            if (_packageLoader.Sessions.TryGetValue(key, out var session))
            {
                if (session.IsLoaded)
                {
                    var candidates = _candidateProjector.Project(package);
                    descriptors.Add(CreateDescriptor(package, LoadingStatus.Loaded, session.LoadedAt, [], candidates, session.ContextKey));
                    continue;
                }

                issueCount++;
                divergenceCount++;
                descriptors.Add(CreateDescriptor(package, LoadingStatus.Failed, session.LoadedAt, BuildDiagnostics(session.LastError), [], session.ContextKey));
                continue;
            }

            staleCount++;
            descriptors.Add(CreateDescriptor(package, LoadingStatus.Stale, null, ["loading-state-missing-for-active-package"], [], null));
        }

        var degraded = issueCount > 0 || staleCount > 0 || divergenceCount > 0;
        var reasonCode = ResolveAvailableReasonCode(issueCount, staleCount, divergenceCount);

        _logger.LogLoadingCatalogRead(correlationId, LoadingCatalogAvailability.Available.ToString(), descriptors.Count, reasonCode);
        _metrics.RecordLoadingCatalogRead(LoadingCatalogAvailability.Available.ToString(), descriptors.Count, degraded, reasonCode);

        return new LoadingCatalogSnapshot(
            LoadingCatalogAvailability.Available,
            DateTimeOffset.UtcNow,
            _refreshTracker.RefreshedAtUtc,
            descriptors,
            null,
            correlationId);
    }

    private static LoadingPackageDescriptor CreateDescriptor(
        ActivePackageDescriptor package,
        LoadingStatus status,
        DateTimeOffset? loadedAtUtc,
        IReadOnlyList<string> diagnostics,
        IReadOnlyList<AssemblyScanCandidate> candidates,
        string? contextKey) =>
        new(
            package.PackageId,
            package.Version,
            status,
            package.InstallPath,
            loadedAtUtc,
            diagnostics,
            candidates,
            contextKey);

    private static IReadOnlyList<string> BuildDiagnostics(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return ["loading-failed"]; 
        }

        return [message.Trim()];
    }

    private static string? ResolveAvailableReasonCode(int issueCount, int staleCount, int divergenceCount)
    {
        if (divergenceCount > 0)
        {
            return "loading-divergence";
        }

        if (staleCount > 0)
        {
            return "loading-state-missing";
        }

        if (issueCount > 0)
        {
            return "loading-catalog-issues";
        }

        return null;
    }
}

