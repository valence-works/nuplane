using Nuplane.Contracts;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Operational;
using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Operational;

/// <summary>
/// Default implementation of <see cref="INuplaneOperationalSurface"/> that delegates
/// to <see cref="OperationalSnapshotProjector"/> for reads and
/// <see cref="ManualReconcileCoordinator"/> for trigger operations.
/// </summary>
internal sealed class NuplaneOperationalSurface(
    OperationalSnapshotProjector projector,
    ManualReconcileCoordinator coordinator,
    IReconciliationLogger logger) : INuplaneOperationalSurface
{
    private readonly OperationalSnapshotProjector _projector = projector ?? throw new ArgumentNullException(nameof(projector));
    private readonly ManualReconcileCoordinator _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    private readonly IReconciliationLogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<OperationalSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var correlationId = CorrelationContext.CreateNew();
        var snapshot = await _projector.ProjectAsync(correlationId, cancellationToken);
        _logger.LogAdminSnapshotRead(correlationId, snapshot.ActivePackages.Count, snapshot.Health.ToString());
        return snapshot;
    }

    /// <inheritdoc />
    public async Task<ManualReconcileOutcome> TriggerReconcileAsync(CancellationToken cancellationToken)
    {
        var correlationId = CorrelationContext.CreateNew();
        return await _coordinator.TriggerAsync(correlationId, cancellationToken);
    }
}
