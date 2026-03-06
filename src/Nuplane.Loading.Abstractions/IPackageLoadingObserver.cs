using Nuplane.Loading.Events;

namespace Nuplane.Loading;

/// <summary>
/// Observer interface for host applications that want to react to package
/// loading events managed by the Nuplane loading domain.
/// All methods have default no-op implementations — implementors only override
/// what they need. Adding new methods in future is non-breaking.
/// </summary>
public interface IPackageLoadingObserver
{
    /// <summary>
    /// Called after a batch of packages has been successfully loaded into
    /// Assembly Load Contexts. Only fires when at least one package was loaded.
    /// </summary>
    /// <param name="evt">The loading event carrying session details for each loaded package.</param>
    /// <param name="cancellationToken">Host shutdown token.</param>
    Task OnPackagesLoadedAsync(
        PackageLoadedEvent evt,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Called when a single package fails to load. One call per failed package.
    /// </summary>
    /// <param name="packageId">The identifier of the package that failed to load.</param>
    /// <param name="reason">The failure reason.</param>
    /// <param name="cancellationToken">Host shutdown token.</param>
    Task OnPackageLoadFailedAsync(
        string packageId,
        string reason,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}
