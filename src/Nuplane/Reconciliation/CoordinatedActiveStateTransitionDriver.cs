using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Operational;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;

namespace Nuplane.Reconciliation;

/// <summary>Runs candidate construction and publication through one retained operation borrow.</summary>
internal sealed class CoordinatedActiveStateTransitionDriver(
    IPhysicalStoreFileSystem files,
    RootMembershipRegistry membershipRegistry,
    ICoordinatedStoreRegistry stateRegistry) : ICoordinatedActiveStateTransitionDriver
{
    private readonly IPhysicalStoreFileSystem _files = files ?? throw new ArgumentNullException(nameof(files));
    private readonly RootMembershipRegistry _membershipRegistry = membershipRegistry ?? throw new ArgumentNullException(nameof(membershipRegistry));
    private readonly ICoordinatedStoreRegistry _stateRegistry = stateRegistry ?? throw new ArgumentNullException(nameof(stateRegistry));

    internal static HashSet<string> BuildDesiredRootIds(
        IReadOnlyList<PackageRequest> requests,
        IReadOnlyList<ResolvedPackageGraph> graphs,
        IReadOnlyList<ResolvedPackageGraphSelection> selections)
    {
        var roots = new HashSet<string>(requests.Select(static request => request.Id), StringComparer.OrdinalIgnoreCase);
        roots.UnionWith(graphs.SelectMany(static graph => graph.Roots).Select(static root => root.PackageId));
        roots.UnionWith(selections.SelectMany(static selection => selection.RootRequests).Select(static request => request.Id));
        return roots;
    }

    internal static HashSet<string> BuildFailedPackageIds(
        IReadOnlyList<string> resolutionFailures,
        IReadOnlyList<string> applyFailures)
        => resolutionFailures.Concat(applyFailures).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyDictionary<string, string>> RestoreFailedRootSubclosureVersionsAsync(
        PackageStoreOperationOwner owner,
        IReadOnlyDictionary<string, string> nextActiveVersions,
        IReadOnlySet<string> desiredRootPackageIds,
        IReadOnlySet<string> failedPackageIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        using var borrow = owner.Borrow();
        var current = await _stateRegistry.ReadCoordinatedStateAsync(borrow, cancellationToken)
            .ConfigureAwait(false);
        return ActivePackageCatalogMapper.RestoreFailedRootSubclosureVersions(
            current, nextActiveVersions, desiredRootPackageIds, failedPackageIds);
    }

    public async Task PreflightAsync(
        PackageStoreOperationOwner owner,
        IReadOnlyDictionary<string, string> nextActiveVersions,
        IReadOnlyList<ResolvedPackage> resolvedPackages,
        PackageChangeSet changeSet,
        IReadOnlyList<ResolvedPackageGraphSelection> graphSelections,
        IReadOnlySet<string> desiredRootPackageIds,
        IReadOnlySet<string> failedPackageIds,
        string correlationId,
        CancellationToken cancellationToken)
    {
        using var borrow = owner.Borrow();
        _ = await CreateCandidateAsync(borrow, nextActiveVersions, resolvedPackages, changeSet,
            graphSelections, desiredRootPackageIds, failedPackageIds, correlationId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task PublishAsync(
        PackageStoreOperationOwner owner,
        IReadOnlyDictionary<string, string> nextActiveVersions,
        IReadOnlyList<ResolvedPackage> successfullyAppliedPackages,
        PackageChangeSet changeSet,
        IReadOnlyList<ResolvedPackageGraphSelection> graphSelections,
        IReadOnlySet<string> desiredRootPackageIds,
        IReadOnlySet<string> failedPackageIds,
        string correlationId,
        CancellationToken cancellationToken)
    {
        using var borrow = owner.Borrow();
        var candidate = await CreateCandidateAsync(borrow, nextActiveVersions, successfullyAppliedPackages,
            changeSet, graphSelections, desiredRootPackageIds, failedPackageIds, correlationId, cancellationToken)
            .ConfigureAwait(false);
        await _stateRegistry.PersistCoordinatedActiveStateAsync(borrow, candidate, cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<StoreStateRecord> CreateCandidateAsync(
        PackageStoreOperationBorrow borrow,
        IReadOnlyDictionary<string, string> nextActiveVersions,
        IReadOnlyList<ResolvedPackage> packages,
        PackageChangeSet changeSet,
        IReadOnlyList<ResolvedPackageGraphSelection> graphSelections,
        IReadOnlySet<string> desiredRootPackageIds,
        IReadOnlySet<string> failedPackageIds,
        string correlationId,
        CancellationToken cancellationToken)
        => CoordinatedActiveStateTransitionProducer.CreateCandidateAsync(
            _files,
            _membershipRegistry,
            _stateRegistry,
            borrow,
            nextActiveVersions,
            packages,
            changeSet,
            graphSelections,
            desiredRootPackageIds,
            failedPackageIds,
            correlationId,
            DateTimeOffset.UtcNow,
            cancellationToken);
}
