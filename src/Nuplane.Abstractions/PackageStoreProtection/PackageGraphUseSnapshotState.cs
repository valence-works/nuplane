namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>Describes whether a core-validated graph-use snapshot is pending or committed.</summary>
public enum PackageGraphUseSnapshotState
{
    /// <summary>The snapshot belongs to work that has not yet committed.</summary>
    Pending,

    /// <summary>The snapshot describes committed package state.</summary>
    Committed
}
