using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Events;

public sealed partial class ObserverEventDispatcher
{
    /// <inheritdoc />
    public async Task PublishPackageLoadingCompletedAsync(
        PackageChangeSet changeSet,
        IReadOnlyList<ResolvedPackage> appliedPackages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        ArgumentNullException.ThrowIfNull(appliedPackages);

        foreach (var observer in _observers.OfType<IPostLoadingNuplaneObserver>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await observer.OnPackageLoadingCompletedAsync(changeSet, appliedPackages, cancellationToken)
                    .ConfigureAwait(false);
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
                _logger.LogObserverError(changeSet.CorrelationId, "OnPackageLoadingCompletedAsync", exception.Message);
            }
        }
    }
}
