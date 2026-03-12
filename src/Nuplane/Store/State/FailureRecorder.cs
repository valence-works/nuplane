namespace Nuplane.Store.State;

/// <summary>
/// Records package failures by persisting them to the store registry.
/// </summary>
public sealed class FailureRecorder(IStoreRegistry storeRegistry) : IFailureRecorder
{
    private readonly IStoreRegistry _storeRegistry = storeRegistry ?? throw new ArgumentNullException(nameof(storeRegistry));

    /// <inheritdoc />
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

        return _storeRegistry.PersistFailureAsync(packageId, stage, message, correlationId, cancellationToken);
    }
}
