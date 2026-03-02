namespace Nuplane.Store.State;

public sealed class FailureRecorder
{
    private readonly StoreRegistry storeRegistry;

    public FailureRecorder(StoreRegistry storeRegistry)
    {
        this.storeRegistry = storeRegistry ?? throw new ArgumentNullException(nameof(storeRegistry));
    }

    public Task RecordAsync(
        string packageId,
        string stage,
        string message,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        return storeRegistry.PersistFailureAsync(packageId, stage, message, correlationId, cancellationToken);
    }
}
