using System;
using System.Threading;
using System.Threading.Tasks;

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
    string CorrelationId);

public sealed record PackageTransactionResult(
    string PackageId,
    string Version,
    bool Succeeded,
    PackageTransactionStage? FailedStage,
    string? FailureMessage,
    bool LastKnownGoodPreserved);

public sealed class PackageTransactionCoordinator
{
    public Task<PackageTransactionResult> ExecuteAsync(
        PackageTransactionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = new PackageTransactionResult(
            request.PackageId,
            request.Version,
            Succeeded: true,
            FailedStage: null,
            FailureMessage: null,
            LastKnownGoodPreserved: true);

        return Task.FromResult(result);
    }
}
