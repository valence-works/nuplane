using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Reconciliation;

/// <summary>Receives exact committed graph selections after reconciliation releases its short store locks.</summary>
/// <remarks>
/// Implementations must load only the supplied graph projections and must transfer each owner only to
/// a lifetime that can retain package paths. This handoff does not authorize reopening or regrouping the store.
/// </remarks>
public interface ILeasedPackageGraphLoadingObserver
{
    /// <summary>Awaits loading for the committed graph selections carried by this cycle.</summary>
    /// <param name="handoff">The immutable graph/request/owner association published by Core.</param>
    /// <param name="currentActiveVersions">
    /// The most recent active package versions read under a new short admission immediately before this call,
    /// or <see langword="null"/> when that optional unload snapshot could not be obtained.
    /// </param>
    /// <param name="cancellationToken">Cancels the awaited load work.</param>
    /// <returns>The package-level failures produced by loading.</returns>
    Task<LeasedPackageGraphLoadingResult> LoadAsync(
        LeasedPackageGraphLoadingHandoff handoff,
        IReadOnlyDictionary<string, string>? currentActiveVersions,
        CancellationToken cancellationToken);
}
