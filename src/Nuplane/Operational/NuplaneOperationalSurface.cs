using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Operational;
using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Operational;

/// <summary>
/// Default implementation of <see cref="global::Nuplane.Extensions.INuplaneOperationalSurface"/> that delegates
/// to <see cref="OperationalSnapshotProjector"/> for reads and
/// <see cref="ManualReconcileCoordinator"/> for trigger operations.
/// </summary>
internal sealed class NuplaneOperationalSurface : global::Nuplane.Extensions.INuplaneOperationalSurface
{
    private readonly OperationalSnapshotProjector _projector;
    private readonly ManualReconcileCoordinator _coordinator;
    private readonly IReconciliationLogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NuplaneOperationalSurface"/> class.
    /// </summary>
    /// <param name="projector">The snapshot projector.</param>
    /// <param name="coordinator">The manual reconcile coordinator.</param>
    /// <param name="logger">The reconciliation logger.</param>
    public NuplaneOperationalSurface(
        OperationalSnapshotProjector projector,
        ManualReconcileCoordinator coordinator,
        IReconciliationLogger logger)
    {
        _projector = projector ?? throw new ArgumentNullException(nameof(projector));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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
