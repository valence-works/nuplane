using Nuplane.Abstractions;
using Nuplane.Store.State;

namespace Nuplane.Reconciliation;

/// <summary>Prepares ordinary package loads before their active versions are persisted.</summary>
internal interface IOrdinaryPackageLoadingActivation
{
    Task<PackageLoadingPreparation?> PrepareAsync(
        IReadOnlyList<ResolvedPackage> packages,
        StoreStateRecord candidate,
        string correlationId,
        CancellationToken cancellationToken);
}

internal sealed record PackageLoadingPreparation(
    IReadOnlyDictionary<string, string> Failures,
    Func<IReadOnlyList<ActivePackage>, IReadOnlySet<string>, IReadOnlyDictionary<string, string>> ValidateForCommit,
    Action Commit,
    IDisposable StagedBatch,
    Func<IReadOnlyDictionary<string, string>, CancellationToken, Task> PublishAsync) : IDisposable
{
    public void Dispose() => StagedBatch.Dispose();
}
