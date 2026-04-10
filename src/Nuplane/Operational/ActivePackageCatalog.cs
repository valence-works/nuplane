using Nuplane.Abstractions;
using Nuplane.Observability;
using Nuplane.Store.State;

namespace Nuplane.Operational;

/// <summary>
/// Default runtime implementation of <see cref="IActivePackageCatalog"/>.
/// Reads the persisted active package descriptor set without replaying observer history.
/// </summary>
public sealed class ActivePackageCatalog(
    IStoreRegistry storeRegistry,
    IReconciliationLogger logger,
    ReconciliationMetrics metrics) : IActivePackageCatalog
{
    private readonly IStoreRegistry _storeRegistry = storeRegistry ?? throw new ArgumentNullException(nameof(storeRegistry));
    private readonly IReconciliationLogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ReconciliationMetrics _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));

    /// <inheritdoc />
    public async Task<ActivePackageCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var correlationId = CorrelationContext.CreateNew();
        var state = await _storeRegistry.GetStateAsync(cancellationToken);
        var snapshot = ActivePackageCatalogMapper.MapSnapshot(state, correlationId);

        var issues = CalculateDescriptorIssues(state);
        _logger.LogActivePackageCatalogRead(correlationId, snapshot.Packages.Count, issues);
        _metrics.RecordActivePackageCatalogRead(snapshot.Packages.Count, issues > 0);

        return snapshot;
    }

    private static int CalculateDescriptorIssues(StoreStateRecord state)
    {
        var issues = 0;
        foreach (var (packageId, version) in state.ActiveVersionById)
        {
            if (!state.ActivePackageDescriptorsByIdNormalized.TryGetValue(packageId, out var descriptor) ||
                !string.Equals(descriptor.Version, version, StringComparison.OrdinalIgnoreCase))
            {
                issues++;
            }
        }

        return issues;
    }
}

