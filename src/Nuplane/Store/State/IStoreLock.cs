namespace Nuplane.Store.State;

/// <summary>
/// Guards a persisted store against concurrent writers from other processes for the duration of a
/// reconciliation cycle.
/// </summary>
/// <remarks>
/// <para>
/// A reconciliation cycle is a read-modify-write over <c>store-state.json</c>, and the file is
/// rewritten with a truncating exclusive <c>File.Create</c>. Two processes reconciling the same
/// store can therefore tear it. The in-process <c>SemaphoreSlim</c> inside one
/// <c>ReconciliationService</c> cannot see the other process, so this is the mechanism that does.
/// </para>
/// <para>
/// Acquisition never blocks: it either succeeds, reports that someone else holds the store, or
/// reports that the store cannot be locked at all. A caller that does not get the lock reports the
/// contention and does nothing, rather than waiting or writing.
/// </para>
/// </remarks>
internal interface IStoreLock
{
    /// <summary>
    /// Tries once, without waiting, to take exclusive use of the store.
    /// </summary>
    /// <returns>
    /// A handle whose <see cref="StoreLockHandle.CanProceed"/> says whether the caller may run, and
    /// which releases the lock when disposed.
    /// </returns>
    StoreLockHandle Acquire();
}
