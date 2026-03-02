using Nuplane.Abstractions;
using Nuplane.Runtime.Observability;

namespace Nuplane.Runtime.Events;

public sealed class PackageChangeEventPublisher
{
    private readonly IReadOnlyList<INuplaneObserver> observers;
    private readonly ReconciliationLogger logger;

    public PackageChangeEventPublisher(IEnumerable<INuplaneObserver> observers, ReconciliationLogger? logger = null)
    {
        this.observers = observers?.ToArray() ?? throw new ArgumentNullException(nameof(observers));
        this.logger = logger ?? new ReconciliationLogger();
    }

    public async Task PublishChangingAsync(PackageChangeSet changeSet, CancellationToken cancellationToken)
    {
        foreach (var observer in observers)
        {
            try
            {
                await observer.OnPackagesChangingAsync(changeSet, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogObserverError(changeSet.CorrelationId, "OnPackagesChangingAsync", ex.Message);
            }
        }
    }

    public async Task PublishChangedAsync(PackageChangeSet changeSet, CancellationToken cancellationToken)
    {
        foreach (var observer in observers)
        {
            try
            {
                await observer.OnPackagesChangedAsync(changeSet, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogObserverError(changeSet.CorrelationId, "OnPackagesChangedAsync", ex.Message);
            }
        }
    }
}
