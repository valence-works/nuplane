using Nuplane.Abstractions;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Reconciliation.LockFile;

internal interface ILockFileCycleCoordinator
{
    Task<LockFileSnapshot> CaptureAsync(CancellationToken cancellationToken);

    PackageRequest ConstrainRequest(LockFileSnapshot snapshot, PackageRequest request);

    LockFileEvaluationResult Evaluate(LockFileSnapshot snapshot, ResolvedPackage resolved);

    Task GenerateAsync(
        LockFileSnapshot snapshot,
        IReadOnlyList<ResolvedPackage> resolvedPackages,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken);
}
