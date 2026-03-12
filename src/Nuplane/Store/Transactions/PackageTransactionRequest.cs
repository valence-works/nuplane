namespace Nuplane.Store.Transactions;

/// <summary>
/// Represents a request to execute a package transaction, including policy gates and an optional stage executor.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="Version">The target version.</param>
/// <param name="CorrelationId">The correlation identifier of the reconciliation cycle.</param>
/// <param name="BlockedByTrustPolicy">Whether the package is blocked by trust policy.</param>
/// <param name="BlockedByLockPolicy">Whether the package is blocked by lock file policy.</param>
/// <param name="PolicyFailureMessage">The policy failure message, if any.</param>
/// <param name="ExpectedArtifactHash">The expected integrity hash from the lock file.</param>
/// <param name="ActualArtifactHash">The actual hash of the resolved artifact.</param>
/// <param name="StageExecutor">An optional delegate to execute at each transaction stage.</param>
public sealed record PackageTransactionRequest(
    string PackageId,
    string Version,
    string CorrelationId,
    bool BlockedByTrustPolicy = false,
    bool BlockedByLockPolicy = false,
    string? PolicyFailureMessage = null,
    string? ExpectedArtifactHash = null,
    string? ActualArtifactHash = null,
    Func<PackageTransactionStage, CancellationToken, Task>? StageExecutor = null);