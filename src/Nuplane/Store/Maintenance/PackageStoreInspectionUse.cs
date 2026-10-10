using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Maintenance;

/// <summary>Detached description of one graph-use record and its nonblocking sentinel classification.</summary>
/// <remarks>This record grants neither package access nor cleanup authority.</remarks>
internal sealed record PackageStoreInspectionUse
{
    internal PackageStoreInspectionUse(
        Guid useId,
        GraphUseRecordOwnershipState ownershipState,
        PackageGraphUseSnapshotState snapshotState,
        ProtectedGraphSnapshot graph)
    {
        if (useId == Guid.Empty)
            throw new ArgumentException("A graph-use identifier cannot be empty.", nameof(useId));
        if (!Enum.IsDefined(ownershipState))
            throw new ArgumentOutOfRangeException(nameof(ownershipState));
        if (!Enum.IsDefined(snapshotState))
            throw new ArgumentOutOfRangeException(nameof(snapshotState));
        ArgumentNullException.ThrowIfNull(graph);
        if (graph.Disposition != ProtectedGraphDisposition.Active || graph.RecoverySelectionEvidence is not null)
            throw new ArgumentException("An inspected graph-use record must contain an active-only graph.", nameof(graph));

        UseId = useId;
        OwnershipState = ownershipState;
        SnapshotState = snapshotState;
        Graph = graph.Copy();
    }

    internal Guid UseId { get; }

    internal GraphUseRecordOwnershipState OwnershipState { get; }

    internal PackageGraphUseSnapshotState SnapshotState { get; }

    internal ProtectedGraphSnapshot Graph { get; }
}
