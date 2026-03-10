using Microsoft.Extensions.Logging;
using Nuplane.Loading.Events;

namespace Nuplane.Loading;

/// <summary>
/// Fans out loading domain events to all registered <see cref="IPackageLoadingObserver"/>
/// instances. Observer exceptions are caught and logged; they never interrupt the dispatch loop.
/// </summary>
internal sealed class LoadingEventDispatcher(
    IEnumerable<IPackageLoadingObserver> observers,
    ILogger<LoadingEventDispatcher> logger) : ILoadingEventDispatcher
{
    private readonly IReadOnlyList<IPackageLoadingObserver> _observers = (observers ?? throw new ArgumentNullException(nameof(observers))).ToList();
    private readonly ILogger<LoadingEventDispatcher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task PublishLoadedAsync(PackageLoadedEvent evt, CancellationToken cancellationToken)
    {
        foreach (var observer in _observers)
        {
            try
            {
                await observer.OnPackagesLoadedAsync(evt, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Observer {Observer} threw in OnPackagesLoadedAsync. CorrelationId={CorrelationId}",
                    observer.GetType().Name, evt.CorrelationId);
            }
        }
    }

    /// <inheritdoc />
    public async Task PublishFailedAsync(string packageId, string reason, CancellationToken cancellationToken)
    {
        foreach (var observer in _observers)
        {
            try
            {
                await observer.OnPackageLoadFailedAsync(packageId, reason, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Observer {Observer} threw in OnPackageLoadFailedAsync for package {PackageId}.",
                    observer.GetType().Name, packageId);
            }
        }
    }
}
