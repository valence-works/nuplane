using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.State;

/// <summary>
/// Records package failures by persisting them to the store registry.
/// </summary>
public sealed class FailureRecorder(IStoreRegistry storeRegistry) : IScopedFailureRecorder
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

    /// <inheritdoc />
    public Task RecordAsync(
        string packageId,
        string stage,
        string message,
        string correlationId,
        PackageStoreOperationBorrow borrow,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentNullException.ThrowIfNull(borrow);

        if (_storeRegistry is not ICoordinatedStoreRegistry coordinated)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "The configured failure recorder does not support coordinated state publication.");

        return coordinated.PersistCoordinatedFailureAsync(borrow, packageId, stage, message, correlationId,
            cancellationToken);
    }
}
