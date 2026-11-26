namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>Describes the package state at the time Core issued a graph-use snapshot.</summary>
public enum PackageGraphUseSnapshotState
{
    /// <summary>Core issued the snapshot before package state was committed.</summary>
    Pending,

    /// <summary>Core issued the snapshot from already committed package state.</summary>
    Committed
}
