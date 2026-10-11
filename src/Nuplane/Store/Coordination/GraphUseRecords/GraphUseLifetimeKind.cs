namespace Nuplane.Store.Coordination.GraphUseRecords;

/// <summary>Identifies the native ownership mechanism represented by a graph-use record.</summary>
internal enum GraphUseLifetimeKind
{
    /// <summary>The record is protected by an exclusive operating-system sentinel handle.</summary>
    OsExclusiveSentinel = 1
}
