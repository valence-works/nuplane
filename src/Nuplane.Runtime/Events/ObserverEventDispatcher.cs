using Nuplane.Abstractions;
using Nuplane.Runtime.Observability;

namespace Nuplane.Runtime.Events;

/// <summary>
/// Dispatches package lifecycle events to all registered observers, catching and logging
/// observer callback errors to prevent individual observer failures from interrupting reconciliation.
/// </summary>
public sealed class ObserverEventDispatcher(IEnumerable<INuplaneObserver> observers, IReconciliationLogger? logger = null) : IObserverEventDispatcher
{
    private readonly IReadOnlyList<INuplaneObserver> _observers = observers?.ToArray() ?? throw new ArgumentNullException(nameof(observers));
    private readonly IReconciliationLogger _logger = logger ?? new ReconciliationLogger();

    /// <inheritdoc />
    public async Task PublishChangingAsync(PackageChangeSet changeSet, CancellationToken cancellationToken)
    {
        foreach (var observer in _observers)
        {
            try
            {
                await observer.OnPackagesChangingAsync(changeSet, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogObserverError(changeSet.CorrelationId, "OnPackagesChangingAsync", ex.Message);
            }
        }
    }

    /// <inheritdoc />
    public async Task PublishChangedAsync(PackageChangeSet changeSet, CancellationToken cancellationToken)
    {
        foreach (var observer in _observers)
        {
            try
            {
                await observer.OnPackagesChangedAsync(changeSet, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogObserverError(changeSet.CorrelationId, "OnPackagesChangedAsync", ex.Message);
            }
        }
    }

    /// <inheritdoc />
    public async Task NotifyPackageFailedAsync(
        string packageId,
        Exception exception,
        string correlationId,
        CancellationToken cancellationToken)
    {
        foreach (var observer in _observers)
        {
            try
            {
                await observer.OnPackageFailedAsync(packageId, exception, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogObserverError(correlationId, "OnPackageFailedAsync", ex.Message);
            }
        }
    }
}
