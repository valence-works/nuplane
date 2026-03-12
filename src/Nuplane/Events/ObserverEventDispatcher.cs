using Nuplane.Abstractions;
using Nuplane.Observability;

namespace Nuplane.Events;

/// <summary>
/// Dispatches package lifecycle events to all registered observers, catching and logging
/// observer callback errors to prevent individual observer failures from interrupting reconciliation.
/// </summary>
public sealed class ObserverEventDispatcher(IEnumerable<INuplaneObserver> observers, IReconciliationLogger? logger = null) : IObserverEventDispatcher
{
    private readonly IReadOnlyList<INuplaneObserver> _observers = (observers ?? throw new ArgumentNullException(nameof(observers))).ToArray();
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

    /// <inheritdoc />
    public async Task PublishReconciledAsync(
        PackageChangeSet changeSet,
        IReadOnlyList<ResolvedPackage> appliedPackages,
        CancellationToken cancellationToken)
    {
        foreach (var observer in _observers)
        {
            try
            {
                await observer.OnPackagesReconciledAsync(changeSet, appliedPackages, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogObserverError(changeSet.CorrelationId, "OnPackagesReconciledAsync", ex.Message);
            }
        }
    }
}
