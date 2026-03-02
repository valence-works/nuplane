using Nuplane.Abstractions;
using Nuplane.Runtime.Observability;

namespace Nuplane.Runtime.Events;

public sealed class ObserverNotifier
{
    private readonly IReadOnlyList<INuplaneObserver> observers;
    private readonly ReconciliationLogger logger;

    public ObserverNotifier(IEnumerable<INuplaneObserver> observers, ReconciliationLogger? logger = null)
    {
        this.observers = observers?.ToArray() ?? throw new ArgumentNullException(nameof(observers));
        this.logger = logger ?? new ReconciliationLogger();
    }

    public async Task NotifyPackageFailedAsync(
        string packageId,
        Exception exception,
        string correlationId,
        CancellationToken cancellationToken)
    {
        foreach (var observer in observers)
        {
            try
            {
                await observer.OnPackageFailedAsync(packageId, exception, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogObserverError(correlationId, "OnPackageFailedAsync", ex.Message);
            }
        }
    }
}
