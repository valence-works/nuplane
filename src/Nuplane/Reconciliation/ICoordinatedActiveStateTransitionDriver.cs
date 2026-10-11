using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Reconciliation;

/// <summary>Preflights and publishes a complete enrolled reconciliation active-state transition.</summary>
internal interface ICoordinatedActiveStateTransitionDriver
{
    Task<IReadOnlyDictionary<string, string>> RestoreFailedRootSubclosureVersionsAsync(
        PackageStoreOperationOwner owner,
        IReadOnlyDictionary<string, string> nextActiveVersions,
        IReadOnlySet<string> desiredRootPackageIds,
        IReadOnlySet<string> failedPackageIds,
        CancellationToken cancellationToken);

    Task PreflightAsync(
        PackageStoreOperationOwner owner,
        IReadOnlyDictionary<string, string> nextActiveVersions,
        IReadOnlyList<ResolvedPackage> resolvedPackages,
        PackageChangeSet changeSet,
        IReadOnlyList<ResolvedPackageGraphSelection> graphSelections,
        IReadOnlySet<string> desiredRootPackageIds,
        IReadOnlySet<string> failedPackageIds,
        string correlationId,
        CancellationToken cancellationToken);

    Task PublishAsync(
        PackageStoreOperationOwner owner,
        IReadOnlyDictionary<string, string> nextActiveVersions,
        IReadOnlyList<ResolvedPackage> successfullyAppliedPackages,
        PackageChangeSet changeSet,
        IReadOnlyList<ResolvedPackageGraphSelection> graphSelections,
        IReadOnlySet<string> desiredRootPackageIds,
        IReadOnlySet<string> failedPackageIds,
        string correlationId,
        CancellationToken cancellationToken);
}
