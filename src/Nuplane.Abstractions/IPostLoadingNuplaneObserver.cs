namespace Nuplane.Abstractions;

/// <summary>Receives package notifications after coordinated package loading has completed.</summary>
/// <remarks>
/// The ordinary scoped observer callbacks still run under the reconciliation owner. This additional callback
/// runs only after coordinated root admission and store locks are released; it must acquire fresh admission for
/// any package-store reads and must not assume the earlier observer borrow remains available.
/// </remarks>
public interface IPostLoadingNuplaneObserver : IScopedNuplaneObserver
{
    /// <summary>Observes reconciled packages after deferred package loading and its failure publication complete.</summary>
    /// <param name="changeSet">The computed package change set for the reconciliation cycle.</param>
    /// <param name="appliedPackages">The packages successfully applied in the cycle; this does not necessarily mean that loading succeeded for each package.</param>
    /// <param name="cancellationToken">A token to cancel the callback.</param>
    Task OnPackageLoadingCompletedAsync(
        PackageChangeSet changeSet,
        IReadOnlyList<ResolvedPackage> appliedPackages,
        CancellationToken cancellationToken);
}
