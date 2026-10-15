using Nuplane.Abstractions;

namespace Nuplane.Events;

/// <summary>Optionally dispatches post-loading notifications without changing the required observer contract.</summary>
public interface IPostLoadingObserverEventDispatcher : IObserverEventDispatcher
{
    /// <summary>Gets whether any registered observer opted into the post-loading notification.</summary>
    bool HasPostLoadingObservers { get; }

    /// <summary>Notifies opted-in observers after coordinated deferred loading and its failure publication complete.</summary>
    /// <param name="changeSet">The computed package change set for the reconciliation cycle.</param>
    /// <param name="appliedPackages">The packages successfully applied in the cycle; this does not necessarily mean that loading succeeded for each package.</param>
    /// <param name="cancellationToken">A token to cancel dispatch.</param>
    Task PublishPackageLoadingCompletedAsync(
        PackageChangeSet changeSet,
        IReadOnlyList<ResolvedPackage> appliedPackages,
        CancellationToken cancellationToken);
}
