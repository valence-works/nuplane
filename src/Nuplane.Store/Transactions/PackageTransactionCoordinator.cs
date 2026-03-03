using Nuplane.Store.Activation;
using Nuplane.Store.State;

namespace Nuplane.Store.Transactions;

/// <summary>
/// Represents the sequential stages of a package transaction.
/// </summary>
public enum PackageTransactionStage
{
    /// <summary>Trust policy gate evaluation stage.</summary>
    TrustPolicyGate,
    /// <summary>Lock file policy gate evaluation stage.</summary>
    LockFileGate,
    /// <summary>Package staging (download/prepare) stage.</summary>
    Stage,
    /// <summary>Package validation (integrity check) stage.</summary>
    Validate,
    /// <summary>Immutable artifact publishing stage.</summary>
    PublishImmutable,
    /// <summary>Atomic version pointer switching stage.</summary>
    AtomicSwitch,
    /// <summary>State persistence stage.</summary>
    PersistState
}

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

/// <summary>
/// Represents the result of a package transaction, including success/failure status and rollback information.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="Version">The target version.</param>
/// <param name="Succeeded">Whether the transaction completed successfully.</param>
/// <param name="FailedStage">The stage at which the transaction failed, if any.</param>
/// <param name="FailureMessage">The failure message, if any.</param>
/// <param name="LastKnownGoodPreserved">Whether the last-known-good version was preserved on failure.</param>
public sealed record PackageTransactionResult(
    string PackageId,
    string Version,
    bool Succeeded,
    PackageTransactionStage? FailedStage,
    string? FailureMessage,
    bool LastKnownGoodPreserved);

/// <summary>
/// Coordinates package transactions through sequential stages, handling trust/lock policy gates,
/// artifact integrity validation, atomic pointer switching, and rollback on failure.
/// </summary>
public sealed class PackageTransactionCoordinator(AtomicPointerSwitcher pointerSwitcher, IFailureRecorder failureRecorder)
{
    private readonly AtomicPointerSwitcher pointerSwitcher = pointerSwitcher ?? throw new ArgumentNullException(nameof(pointerSwitcher));
    private readonly IFailureRecorder failureRecorder = failureRecorder ?? throw new ArgumentNullException(nameof(failureRecorder));

    /// <summary>
    /// Executes a package transaction, processing stages sequentially and rolling back
    /// to the previous version on failure.
    /// </summary>
    /// <param name="request">The transaction request.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The transaction result.</returns>
    public Task<PackageTransactionResult> ExecuteAsync(
        PackageTransactionRequest request,
        CancellationToken cancellationToken) => ExecuteInternalAsync(request, cancellationToken);

    private async Task<PackageTransactionResult> ExecuteInternalAsync(
        PackageTransactionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var currentPointer = pointerSwitcher.GetCurrentVersion(request.PackageId);

        try
        {
            if (!string.IsNullOrWhiteSpace(request.ExpectedArtifactHash) &&
                !string.IsNullOrWhiteSpace(request.ActualArtifactHash) &&
                !string.Equals(request.ExpectedArtifactHash, request.ActualArtifactHash, StringComparison.OrdinalIgnoreCase))
            {
                return await BlockByPolicyAsync(
                    request with { PolicyFailureMessage = "Lock hash mismatch detected during validation." },
                    currentPointer,
                    PackageTransactionStage.LockFileGate,
                    cancellationToken);
            }

            if (request.BlockedByTrustPolicy)
            {
                return await BlockByPolicyAsync(
                    request,
                    currentPointer,
                    PackageTransactionStage.TrustPolicyGate,
                    cancellationToken);
            }

            if (request.BlockedByLockPolicy)
            {
                return await BlockByPolicyAsync(
                    request,
                    currentPointer,
                    PackageTransactionStage.LockFileGate,
                    cancellationToken);
            }

            await ExecuteStageAsync(request, PackageTransactionStage.Stage, cancellationToken);
            await ExecuteStageAsync(request, PackageTransactionStage.Validate, cancellationToken);
            await ExecuteStageAsync(request, PackageTransactionStage.PublishImmutable, cancellationToken);
            await ExecuteStageAsync(request, PackageTransactionStage.AtomicSwitch, cancellationToken);
            await pointerSwitcher.SwitchAsync(request.PackageId, request.Version, cancellationToken);
            await ExecuteStageAsync(request, PackageTransactionStage.PersistState, cancellationToken);

            return new(
                request.PackageId,
                request.Version,
                Succeeded: true,
                FailedStage: null,
                FailureMessage: null,
                LastKnownGoodPreserved: false);
        }
        catch (Exception ex)
        {
            var failedStageName = ex is PackageTransactionStageException stageException
                ? stageException.Stage.ToString()
                : PackageTransactionStage.Stage.ToString();

            await failureRecorder.RecordAsync(
                request.PackageId,
                failedStageName,
                ex.Message,
                request.CorrelationId,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(currentPointer))
            {
                await pointerSwitcher.SwitchAsync(request.PackageId, currentPointer, cancellationToken);
            }

            return new(
                request.PackageId,
                request.Version,
                Succeeded: false,
                FailedStage: ex is PackageTransactionStageException stageFailure ? stageFailure.Stage : null,
                FailureMessage: ex.Message,
                LastKnownGoodPreserved: !string.IsNullOrWhiteSpace(currentPointer));
        }
    }

    private async Task<PackageTransactionResult> BlockByPolicyAsync(
        PackageTransactionRequest request,
        string? currentPointer,
        PackageTransactionStage stage,
        CancellationToken cancellationToken)
    {
        var message = string.IsNullOrWhiteSpace(request.PolicyFailureMessage)
            ? "Package transaction blocked by policy gate."
            : request.PolicyFailureMessage;

        await failureRecorder.RecordAsync(
            request.PackageId,
            stage.ToString(),
            message,
            request.CorrelationId,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(currentPointer))
        {
            await pointerSwitcher.SwitchAsync(request.PackageId, currentPointer, cancellationToken);
        }

        return new(
            request.PackageId,
            request.Version,
            Succeeded: false,
            FailedStage: stage,
            FailureMessage: message,
            LastKnownGoodPreserved: !string.IsNullOrWhiteSpace(currentPointer));
    }

    private static async Task ExecuteStageAsync(PackageTransactionRequest request, PackageTransactionStage stage, CancellationToken cancellationToken)
    {
        try
        {
            if (request.StageExecutor is null)
            {
                return;
            }

            await request.StageExecutor(stage, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new PackageTransactionStageException(stage, ex.Message, ex);
        }
    }

    private sealed class PackageTransactionStageException(PackageTransactionStage stage, string message, Exception innerException)
        : Exception(message, innerException)
    {
        public PackageTransactionStage Stage { get; } = stage;
    }
}
