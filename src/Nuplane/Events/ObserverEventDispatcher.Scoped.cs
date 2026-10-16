using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Events;

public sealed partial class ObserverEventDispatcher
{
    /// <inheritdoc />
    public Task PublishChangingAsync(PackageChangeSet changeSet, PackageStoreOperationOwner owner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        return DispatchScopedAsync(owner, changeSet.CorrelationId, "OnPackagesChangingAsync",
            (observer, borrow) => observer.OnPackagesChangingAsync(changeSet, borrow, cancellationToken),
            observer => observer.OnPackagesChangingAsync(changeSet, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task PublishChangedAsync(PackageChangeSet changeSet, PackageStoreOperationOwner owner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        return DispatchScopedAsync(owner, changeSet.CorrelationId, "OnPackagesChangedAsync",
            (observer, borrow) => observer.OnPackagesChangedAsync(changeSet, borrow, cancellationToken),
            observer => observer.OnPackagesChangedAsync(changeSet, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task NotifyPackageFailedAsync(string packageId, Exception exception, string correlationId,
        PackageStoreOperationOwner owner, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        return DispatchScopedAsync(owner, correlationId, "OnPackageFailedAsync",
            (observer, borrow) => observer.OnPackageFailedAsync(packageId, exception, borrow, cancellationToken),
            observer => observer.OnPackageFailedAsync(packageId, exception, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task PublishReconciledAsync(PackageChangeSet changeSet, IReadOnlyList<ResolvedPackage> appliedPackages,
        PackageStoreOperationOwner owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        ArgumentNullException.ThrowIfNull(appliedPackages);
        return DispatchScopedAsync(owner, changeSet.CorrelationId, "OnPackagesReconciledAsync",
            (observer, borrow) => observer.OnPackagesReconciledAsync(changeSet, appliedPackages, borrow, cancellationToken),
            observer => observer.OnPackagesReconciledAsync(changeSet, appliedPackages, cancellationToken), cancellationToken);
    }

    // The runtime calls this before its first source/resolver callback. Each dispatch also checks
    // the whole immutable observer set before invoking its first observer.
    internal void ValidateCoordinatedParticipants()
    {
        if (_observers.Any(static observer =>
                observer is not IScopedNuplaneObserver and not IPackagePathIndependentNuplaneObserver))
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "Every enrolled observer must declare scoped access or package-path independence.");
        }
    }

    void IScopedObserverEventDispatcher.ValidateCoordinatedParticipants() => ValidateCoordinatedParticipants();

    private async Task DispatchScopedAsync(PackageStoreOperationOwner owner, string correlationId, string callbackName,
        Func<IScopedNuplaneObserver, PackageStoreOperationBorrow, Task> scopedCallback,
        Func<INuplaneObserver, Task> independentCallback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ValidateCoordinatedParticipants();
        foreach (var observer in _observers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var borrow = owner.Borrow();
            try
            {
                if (observer is IScopedNuplaneObserver scoped)
                    await scopedCallback(scoped, borrow).ConfigureAwait(false);
                else
                    await independentCallback(observer).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (PackageStoreAdmissionException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogObserverError(correlationId, callbackName, exception.Message);
            }
        }
    }
}
