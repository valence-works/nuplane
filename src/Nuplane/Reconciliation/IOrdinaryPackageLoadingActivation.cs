using Nuplane.Abstractions;
using Nuplane.Store.State;

namespace Nuplane.Reconciliation;

/// <summary>Prepares ordinary package loads before their active versions are persisted.</summary>
internal interface IOrdinaryPackageLoadingActivation
{
    Task<OrdinaryPackageLoadingResult?> PrepareAsync(
        IReadOnlyList<ResolvedPackage> packages,
        StoreStateRecord candidate,
        string correlationId,
        CancellationToken cancellationToken);

    void DiscardInactiveLoads(IReadOnlyDictionary<string, string> activeVersions);
}

internal sealed record OrdinaryPackageLoadingResult(
    IReadOnlyDictionary<string, string> Failures,
    Func<IReadOnlyDictionary<string, string>, CancellationToken, Task> PublishAsync);
