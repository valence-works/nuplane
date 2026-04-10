using Nuplane.Abstractions;
using Nuplane.Observability;
using Nuplane.Operational;
using Nuplane.Reconciliation;

namespace Nuplane.Admin;

/// <summary>
/// Default implementation of <see cref="INuplaneAdminOperations"/> that delegates
/// to standalone package/state services for reads and
/// <see cref="ManualReconcileCoordinator"/> for trigger operations.
/// </summary>
internal sealed class NuplaneAdminOperations(
    IActivePackageCatalog activePackageCatalog,
    OperationalSnapshotProjector projector,
    ManualReconcileCoordinator coordinator) : INuplaneAdminOperations
{
    private readonly IActivePackageCatalog _activePackageCatalog = activePackageCatalog ?? throw new ArgumentNullException(nameof(activePackageCatalog));
    private readonly OperationalSnapshotProjector _projector = projector ?? throw new ArgumentNullException(nameof(projector));
    private readonly ManualReconcileCoordinator _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <inheritdoc />
    public Task<ActivePackageCatalogSnapshot> GetPackagesAsync(CancellationToken cancellationToken)
    {
        return _activePackageCatalog.GetSnapshotAsync(cancellationToken);
    }


    /// <inheritdoc />
    public async Task<OperationalStateSnapshot> GetStateAsync(CancellationToken cancellationToken)
    {
        var correlationId = CorrelationContext.CreateNew();
        return await _projector.ProjectAsync(correlationId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ManualReconcileOutcome> TriggerReconcileAsync(CancellationToken cancellationToken)
    {
        var correlationId = CorrelationContext.CreateNew();
        return await _coordinator.TriggerAsync(correlationId, cancellationToken);
    }
}
