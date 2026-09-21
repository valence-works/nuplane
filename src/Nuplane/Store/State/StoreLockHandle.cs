namespace Nuplane.Store.State;

/// <summary>
/// The lease an <see cref="IStoreLock"/> hands out. Disposing it releases the underlying exclusive
/// file handle, which is why callers take it with <c>using</c>: the lock is then released on every
/// exit path, including an exception out of the reconciliation pipeline and cancellation.
/// </summary>
internal sealed class StoreLockHandle : IDisposable
{
    private readonly FileStream? _stream;

    internal StoreLockHandle(StoreLockOutcome outcome, string? lockFilePath, FileStream? stream)
    {
        Outcome = outcome;
        LockFilePath = lockFilePath;
        _stream = stream;
    }

    /// <summary>
    /// Gets what happened when the lock was requested.
    /// </summary>
    public StoreLockOutcome Outcome { get; }

    /// <summary>
    /// Gets the lock file this handle refers to, or <see langword="null"/> when there was no store
    /// to lock.
    /// </summary>
    public string? LockFilePath { get; }

    /// <summary>
    /// Gets whether the caller may run against the store. This is <see langword="false"/> only for
    /// <see cref="StoreLockOutcome.Unavailable"/>: a store that cannot be locked at all
    /// (<see cref="StoreLockOutcome.NotLockable"/>) still proceeds, so a host whose state directory
    /// is not writable keeps working exactly as it did before the store lock existed.
    /// </summary>
    public bool CanProceed => Outcome != StoreLockOutcome.Unavailable;

    /// <summary>
    /// Creates the handle for a composition with no store to lock — in-memory persistence, or the
    /// store lock switched off.
    /// </summary>
    public static StoreLockHandle NotRequired() => new(StoreLockOutcome.NotRequired, null, null);

    /// <summary>
    /// Releases the exclusive handle, if one was taken. The lock file itself is left behind: it is
    /// empty, and deleting it would race another process that is about to open it.
    /// </summary>
    public void Dispose() => _stream?.Dispose();
}
