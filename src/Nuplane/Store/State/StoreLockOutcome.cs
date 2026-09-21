namespace Nuplane.Store.State;

/// <summary>
/// The outcome of asking <see cref="IStoreLock"/> for exclusive use of a persisted store, so that
/// only one process at a time runs the reconciliation pipeline against a given
/// <c>store-state.json</c>.
/// </summary>
public enum StoreLockOutcome
{
    /// <summary>
    /// There is nothing to lock: the store is in-memory only, or the store lock is switched off by
    /// <see cref="Nuplane.Reconciliation.Configuration.ReconciliationOptions.EnableStoreLock"/>.
    /// The caller proceeds, exactly as it did before the store lock existed.
    /// </summary>
    NotRequired,

    /// <summary>
    /// The lock is held by this caller until the handle is disposed. The caller proceeds.
    /// </summary>
    Acquired,

    /// <summary>
    /// Another process — or another composition in this process — holds the lock on the same store.
    /// The caller does not proceed and reports the contention instead of writing.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The lock file could not be created or opened at all, for example because its directory is
    /// read-only. The caller proceeds unprotected, because refusing would break a deployment that
    /// works today; the condition is logged as a warning.
    /// </summary>
    NotLockable
}
