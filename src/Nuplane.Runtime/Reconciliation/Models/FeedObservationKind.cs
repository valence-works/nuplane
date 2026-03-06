namespace Nuplane.Runtime.Reconciliation.Models;

/// <summary>
/// Describes the observation mechanism that detected a feed change.
/// </summary>
public enum FeedObservationKind
{
    /// <summary>A local directory watcher detected a file-system change.</summary>
    DirectoryWatcher,

    /// <summary>A feed-specific notification channel detected a change.</summary>
    Notification,

    /// <summary>A feed-specific polling monitor detected a change outside the scheduled convergence loop.</summary>
    Polling
}

