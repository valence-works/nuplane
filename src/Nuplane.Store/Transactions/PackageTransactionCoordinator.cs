using Nuplane.Store.Activation;
using Nuplane.Store.State;

namespace Nuplane.Store.Transactions;

public enum PackageTransactionStage
{
    TrustPolicyGate,
    LockFileGate,
    Stage,
    Validate,
    PublishImmutable,
    AtomicSwitch,
    PersistState
}

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

public sealed record PackageTransactionResult(
    string PackageId,
    string Version,
    bool Succeeded,
    PackageTransactionStage? FailedStage,
    string? FailureMessage,
    bool LastKnownGoodPreserved);

public sealed class PackageTransactionCoordinator
{
    private readonly AtomicPointerSwitcher pointerSwitcher;
    private readonly FailureRecorder failureRecorder;

    public PackageTransactionCoordinator(AtomicPointerSwitcher pointerSwitcher, FailureRecorder failureRecorder)
    {
        this.pointerSwitcher = pointerSwitcher ?? throw new ArgumentNullException(nameof(pointerSwitcher));
        this.failureRecorder = failureRecorder ?? throw new ArgumentNullException(nameof(failureRecorder));
    }

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
