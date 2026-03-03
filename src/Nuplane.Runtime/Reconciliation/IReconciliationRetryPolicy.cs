namespace Nuplane.Runtime.Reconciliation;

/// <summary>
/// Defines a retry policy for transient failures during reconciliation operations.
/// </summary>
public interface IReconciliationRetryPolicy
{
    /// <summary>
    /// Executes an operation with automatic retry on transient failures using exponential backoff.
    /// </summary>
    /// <typeparam name="T">The return type of the operation.</typeparam>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result of the operation.</returns>
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);
}
