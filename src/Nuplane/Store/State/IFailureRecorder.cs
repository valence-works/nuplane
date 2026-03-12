namespace Nuplane.Store.State;

/// <summary>
/// Defines the contract for recording package processing failures during reconciliation.
/// </summary>
public interface IFailureRecorder
{
    /// <summary>
    /// Records a failure for the specified package.
    /// </summary>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="stage">The reconciliation stage where the failure occurred.</param>
    /// <param name="message">A descriptive error message.</param>
    /// <param name="correlationId">The correlation identifier of the reconciliation cycle.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task RecordAsync(
        string packageId,
        string stage,
        string message,
        string correlationId,
        CancellationToken cancellationToken);
}
