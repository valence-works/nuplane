namespace Nuplane.Store.State;

/// <summary>
/// Refreshes a file-backed store registry at the start of a lock-held state cycle.
/// </summary>
internal interface IStoreStateCycleRefresher
{
    /// <summary>
    /// Reloads persisted state when the registry is file-backed.
    /// </summary>
    /// <returns><see langword="true"/> when state was reloaded; otherwise, <see langword="false"/>.</returns>
    Task<bool> RefreshFromDiskAsync(CancellationToken cancellationToken);
}
