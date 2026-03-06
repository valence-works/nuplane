namespace Nuplane.Loading;

/// <summary>
/// Fans out loading domain events to all registered <see cref="IPackageLoadingObserver"/>
/// instances. Follows the same pattern as <c>IObserverEventDispatcher</c> in the runtime.
/// </summary>
public interface ILoadingEventDispatcher
{
    /// <summary>Publish a <see cref="PackageLoadedEvent"/> to all observers.</summary>
    /// <param name="evt">The loading event to publish.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task PublishLoadedAsync(
        PackageLoadedEvent evt,
        CancellationToken cancellationToken);

    /// <summary>Notify observers of a per-package load failure.</summary>
    /// <param name="packageId">The identifier of the package that failed to load.</param>
    /// <param name="reason">The failure reason.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task PublishFailedAsync(
        string packageId,
        string reason,
        CancellationToken cancellationToken);
}
