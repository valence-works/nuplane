using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Sources;

/// <summary>Aggregates desired requests while retaining one admitted package-store operation.</summary>
/// <remarks>
/// Implementations validate every source before invoking the first callback, await each scoped
/// source while its borrow is live, and propagate typed package-store admission refusals.
/// </remarks>
public interface IScopedDesiredStateAggregator : IDesiredStateAggregator
{
    /// <summary>Aggregates desired requests under an already-admitted operation owner.</summary>
    /// <param name="sources">The original desired sources, without identity-changing wrappers.</param>
    /// <param name="owner">The configured-root owner retained by the caller for this operation.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The aggregate result and ordinary per-source read errors.</returns>
    /// <exception cref="PackageStoreAdmissionException">A source lacks a scoped contract or scoped access is refused.</exception>
    Task<DesiredAggregateResult> AggregateAsync(
        IEnumerable<IDesiredPackageSource> sources,
        PackageStoreOperationOwner owner,
        CancellationToken cancellationToken);
}
