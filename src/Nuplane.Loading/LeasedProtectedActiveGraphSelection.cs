using System.Collections.ObjectModel;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Loading;

/// <summary>One persisted active graph joined to its exact native graph-use owner.</summary>
internal sealed class LeasedProtectedActiveGraphSelection(
    ProtectedGraphSnapshot graph,
    IReadOnlyList<ResolvedPackage> packages,
    IReadOnlyDictionary<Guid, string> exactPathsByNode,
    PackageGraphUseLeaseOwner leaseOwner)
{
    internal ProtectedGraphSnapshot Graph { get; } = graph ?? throw new ArgumentNullException(nameof(graph));
    internal IReadOnlyList<ResolvedPackage> Packages { get; } = packages ?? throw new ArgumentNullException(nameof(packages));
    internal IReadOnlyDictionary<Guid, string> ExactPathsByNode { get; } = new ReadOnlyDictionary<Guid, string>(
        new Dictionary<Guid, string>(exactPathsByNode ?? throw new ArgumentNullException(nameof(exactPathsByNode))));
    internal PackageGraphUseLeaseOwner LeaseOwner { get; } = leaseOwner ?? throw new ArgumentNullException(nameof(leaseOwner));
}
