using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Store.Coordination;

/// <summary>Publishes native protection for one actual resolved package graph before its retained reads begin.</summary>
/// <remarks>
/// The graph and its original requests are the authority input. Callers cannot mint a snapshot or supply
/// replacement paths; Core observes and binds each selected graph node while the admitted root borrow is live.
/// </remarks>
public interface IResolvedPackageGraphUseLeaseAcquisition
{
    /// <summary>Publishes the graph-use record and returns counted read and release ownership.</summary>
    /// <param name="borrow">A live root-operation borrow for the graph's physical install root.</param>
    /// <param name="graph">The complete resolved graph whose exact paths will be observed by Core.</param>
    /// <param name="originalRootRequests">The requests that selected every root in the graph.</param>
    /// <param name="snapshotState">Whether the published graph use is pending or committed.</param>
    /// <param name="cancellationToken">Cancels acquisition before ownership is returned.</param>
    /// <returns>An owner that must be disposed or transferred to the actual reader lifetime.</returns>
    /// <exception cref="PackageStoreAdmissionException">The borrow, graph, or native root authority is invalid.</exception>
    ValueTask<PackageGraphUseLeaseOwner> AcquireForRootAsync(
        PackageStoreOperationBorrow borrow,
        ResolvedPackageGraph graph,
        IReadOnlyList<PackageRequest> originalRootRequests,
        PackageGraphUseSnapshotState snapshotState,
        CancellationToken cancellationToken = default);
}
