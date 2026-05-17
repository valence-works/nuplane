using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Observability;

namespace Nuplane.Loading;

/// <summary>
/// Default runtime implementation of <see cref="IPackageLoadStateCatalog"/>.
/// Reports current-process load state and deterministic assembly-reference guidance for the active package set.
/// </summary>
internal sealed class LoadingCatalog(
    IActivePackageCatalog activePackageCatalog,
    PackageLoader packageLoader,
    AssemblyScanCandidateProjector candidateProjector,
    LoadingCatalogRefreshTracker refreshTracker,
    IOptions<LoadingOptions> options,
    IReconciliationLogger logger,
    ReconciliationMetrics metrics) : ILoadingCatalog, IPackageLoadStateCatalog
{
    private readonly IActivePackageCatalog _activePackageCatalog = activePackageCatalog ?? throw new ArgumentNullException(nameof(activePackageCatalog));
    private readonly PackageLoader _packageLoader = packageLoader ?? throw new ArgumentNullException(nameof(packageLoader));
    private readonly AssemblyScanCandidateProjector _candidateProjector = candidateProjector ?? throw new ArgumentNullException(nameof(candidateProjector));
    private readonly LoadingCatalogRefreshTracker _refreshTracker = refreshTracker ?? throw new ArgumentNullException(nameof(refreshTracker));
    private readonly LoadingOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly IReconciliationLogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ReconciliationMetrics _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));

    /// <inheritdoc />
    public async Task<PackageLoadStateSnapshot> GetLoadStateAsync(CancellationToken cancellationToken)
    {
        var correlationId = CorrelationContext.CreateNew();
        var activeSnapshot = await _activePackageCatalog.GetActivePackagesAsync(cancellationToken);

        if (!_options.Enabled)
        {
            var disabledPackages = activeSnapshot.Packages
                .Select(package => CreateDescriptor(package, PackageLoadStatus.Disabled, null, ["loading-disabled"], []))
                .ToArray();

            _logger.LogLoadingCatalogRead(correlationId, LoadingCatalogAvailability.Disabled.ToString(), disabledPackages.Length, "loading-disabled");
            _metrics.RecordLoadingCatalogRead(LoadingCatalogAvailability.Disabled.ToString(), disabledPackages.Length, degraded: false, reasonCode: "loading-disabled");

            return new PackageLoadStateSnapshot(
                PackageLoadStateAvailability.Disabled,
                DateTimeOffset.UtcNow,
                _refreshTracker.RefreshedAtUtc,
                disabledPackages,
                "loading-disabled",
                correlationId);
        }

        if (!_refreshTracker.HasRefreshed)
        {
            var stalePackages = activeSnapshot.Packages
                .Select(package => CreateDescriptor(package, PackageLoadStatus.Stale, null, ["loading-not-refreshed-for-current-process"], []))
                .ToArray();

            _logger.LogLoadingCatalogRead(correlationId, LoadingCatalogAvailability.Stale.ToString(), stalePackages.Length, "loading-stale");
            _metrics.RecordLoadingCatalogRead(LoadingCatalogAvailability.Stale.ToString(), stalePackages.Length, degraded: stalePackages.Length > 0, reasonCode: "loading-stale");

            return new PackageLoadStateSnapshot(
                PackageLoadStateAvailability.Stale,
                DateTimeOffset.UtcNow,
                null,
                stalePackages,
                "loading-stale",
                correlationId);
        }

        var descriptors = new List<PackageLoadState>(activeSnapshot.Packages.Count);
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
                    descriptors.Add(CreateDescriptor(package, PackageLoadStatus.Loaded, session.LoadedAt, [], candidates, session));
                    continue;
                }

                issueCount++;
                divergenceCount++;
                descriptors.Add(CreateDescriptor(package, PackageLoadStatus.Failed, session.LoadedAt, BuildDiagnostics(session.LastError), [], session));
                continue;
            }

            staleCount++;
            descriptors.Add(CreateDescriptor(package, PackageLoadStatus.Stale, null, ["loading-state-missing-for-active-package"], []));
        }

        var degraded = issueCount > 0 || staleCount > 0 || divergenceCount > 0;
        var reasonCode = ResolveAvailableReasonCode(issueCount, staleCount, divergenceCount);

        _logger.LogLoadingCatalogRead(correlationId, LoadingCatalogAvailability.Available.ToString(), descriptors.Count, reasonCode);
        _metrics.RecordLoadingCatalogRead(LoadingCatalogAvailability.Available.ToString(), descriptors.Count, degraded, reasonCode);

        return new PackageLoadStateSnapshot(
            PackageLoadStateAvailability.Available,
            DateTimeOffset.UtcNow,
            _refreshTracker.RefreshedAtUtc,
            descriptors,
            null,
            correlationId);
    }

    /// <summary>
    /// Reads the current loading snapshot using legacy naming.
    /// </summary>
    public async Task<LoadingCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = await GetLoadStateAsync(cancellationToken).ConfigureAwait(false);

        return new LoadingCatalogSnapshot(
            snapshot.Availability switch
            {
                PackageLoadStateAvailability.Disabled => LoadingCatalogAvailability.Disabled,
                PackageLoadStateAvailability.Stale => LoadingCatalogAvailability.Stale,
                PackageLoadStateAvailability.Available => LoadingCatalogAvailability.Available,
                _ => throw new ArgumentOutOfRangeException(nameof(snapshot))
            },
            snapshot.SnapshotAtUtc,
            snapshot.RefreshedAtUtc,
            snapshot.Packages.Select(static package => new LoadingPackageDescriptor(
                package.PackageId,
                package.Version,
                package.Status switch
                {
                    PackageLoadStatus.Disabled => LoadingStatus.Disabled,
                    PackageLoadStatus.Stale => LoadingStatus.Stale,
                    PackageLoadStatus.Loaded => LoadingStatus.Loaded,
                    PackageLoadStatus.Failed => LoadingStatus.Failed,
                    _ => throw new ArgumentOutOfRangeException(nameof(package))
                },
                package.InstallPath,
                package.LoadedAtUtc,
                package.Diagnostics,
                package.AssemblyReferences.Select(static reference => reference.ToCandidate()).ToArray(),
                null,
                package.LoadMode,
                package.FrameworkIntegrationSafe,
                package.LoadModeDiagnostics)).ToArray(),
            snapshot.Reason,
            snapshot.CorrelationId);
    }

    private static PackageLoadState CreateDescriptor(
        ActivePackage package,
        PackageLoadStatus status,
        DateTimeOffset? loadedAtUtc,
        IReadOnlyList<string> diagnostics,
        IReadOnlyList<PackageAssemblyReference> assemblyReferences,
        PackageLoadSession? session = null) =>
        new PackageLoadState(
            package.PackageId,
            package.Version,
            status,
            package.InstallPath,
            loadedAtUtc,
            diagnostics,
            assemblyReferences.ToArray(),
            package.Discoverable,
            session?.LoadMode ?? PackageLoadMode.Collectible,
            session?.FrameworkIntegrationSafe ?? false)
        {
            LoadModeDiagnostics = session?.LoadModeDiagnostics ?? []
        };

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
