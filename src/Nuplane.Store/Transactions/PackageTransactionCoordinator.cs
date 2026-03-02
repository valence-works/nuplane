using System;
using System.Threading;
using System.Threading.Tasks;
using Nuplane.Store.Activation;
using Nuplane.Store.State;

namespace Nuplane.Store.Transactions;

public enum PackageTransactionStage
{
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
    Func<PackageTransactionStage, Task>? stageExecutor = null);

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
            await ExecuteStageAsync(request, PackageTransactionStage.Stage);
            await ExecuteStageAsync(request, PackageTransactionStage.Validate);
            await ExecuteStageAsync(request, PackageTransactionStage.PublishImmutable);
            await ExecuteStageAsync(request, PackageTransactionStage.AtomicSwitch);
            await pointerSwitcher.SwitchAsync(request.PackageId, request.Version, cancellationToken);
            await ExecuteStageAsync(request, PackageTransactionStage.PersistState);

            return new PackageTransactionResult(
                request.PackageId,
                request.Version,
                Succeeded: true,
                FailedStage: null,
                FailureMessage: null,
                LastKnownGoodPreserved: true);
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

            return new PackageTransactionResult(
                request.PackageId,
                request.Version,
                Succeeded: false,
                FailedStage: ex is PackageTransactionStageException stageFailure ? stageFailure.Stage : null,
                FailureMessage: ex.Message,
                LastKnownGoodPreserved: true);
        }
    }

    private static async Task ExecuteStageAsync(PackageTransactionRequest request, PackageTransactionStage stage)
    {
        try
        {
            if (request.stageExecutor is null)
            {
                return;
            }

            await request.stageExecutor(stage);
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
