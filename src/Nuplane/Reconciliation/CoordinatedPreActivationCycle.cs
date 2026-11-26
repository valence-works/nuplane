using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Reconciliation.Middleware;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Coordination;
using Nuplane.Store.State;

namespace Nuplane.Reconciliation;

/// <summary>Retains the exact pending installs across the unlocked loading phase of a coordinated cycle.</summary>
internal sealed class CoordinatedPreActivationCycle : IAsyncDisposable
{
    private readonly string _ledgerDigest;
    private readonly PackageGraphUseLeaseOwner[] _leases;

    private CoordinatedPreActivationCycle(
        StoreStateRecord priorState,
        string ledgerDigest,
        LeasedPackageGraphLoadingHandoff handoff,
        PackageGraphUseLeaseOwner[] leases)
    {
        PriorState = priorState;
        _ledgerDigest = ledgerDigest;
        Handoff = handoff;
        _leases = leases;
    }

    internal StoreStateRecord PriorState { get; }
    internal LeasedPackageGraphLoadingHandoff Handoff { get; }

    internal static async Task<CoordinatedPreActivationCycle> PrepareAsync(
        PackageStoreOperationOwner owner,
        ICoordinatedStoreRegistry registry,
        IResolvedPackageGraphUseLeaseAcquisition acquisition,
        ReconciliationCycleContext context,
        CancellationToken cancellationToken)
    {
        using var borrow = owner.Borrow();
        // Source snapshots and acquisition failures have already been persisted in this phase.
        // Use their resulting exact member state as the revision to compare at commit.
        var prior = await registry.ReadCoordinatedStateAsync(borrow, cancellationToken).ConfigureAwait(false);
        if (prior.ProtectionRecord is null)
            throw Stale(owner, "The coordinated activation has no verified current member protection.");

        var selections = context.ApplyResult!.SuccessfulGraphSelections;
        var pending = new List<LeasedPackageGraphSelection>(selections.Count);
        var leases = new List<PackageGraphUseLeaseOwner>(selections.Count);
        try
        {
            foreach (var selection in selections)
            {
                var lease = await acquisition.AcquireForRootAsync(
                    borrow, selection.Graph, selection.RootRequests, PackageGraphUseSnapshotState.Pending,
                    cancellationToken).ConfigureAwait(false);
                leases.Add(lease);
                pending.Add(new(selection, lease));
            }

            var ledgerDigest = PackageStoreOperationAccess.GetLockedMemberLocations(borrow).Ledger.LedgerDigest;
            return new(prior, ledgerDigest,
                new LeasedPackageGraphLoadingHandoff(context.ChangeSet!, context.DesiredRequests, pending),
                leases.ToArray());
        }
        catch (Exception primary)
        {
            try
            {
                await DisposeLeasesAsync(leases).ConfigureAwait(false);
            }
            catch (Exception cleanup)
            {
                throw new AggregateException("Pending package graphs could not be prepared or fully released.", primary, cleanup);
            }

            throw;
        }
    }

    internal async Task<StoreStateRecord> ReadUnchangedStateAsync(
        PackageStoreOperationOwner owner,
        ICoordinatedStoreRegistry registry,
        CancellationToken cancellationToken)
    {
        using var borrow = owner.Borrow();
        var current = await registry.ReadCoordinatedStateAsync(borrow, cancellationToken).ConfigureAwait(false);
        var ledger = PackageStoreOperationAccess.GetLockedMemberLocations(borrow).Ledger;
        var priorProtection = PriorState.ProtectionRecord!;
        if (owner.Root != priorProtection.RootIdentity || owner.Epoch != priorProtection.EnrollmentEpoch ||
            !StringComparer.Ordinal.Equals(ledger.LedgerDigest, _ledgerDigest) ||
            current.ProtectionRecord is not { } protection ||
            !priorProtection.HasSamePayloadAs(protection))
        {
            throw Stale(owner, "The coordinated member or root changed while package loading was staged. Retry reconciliation against current state.");
        }

        return current;
    }

    internal async Task ApplyFailuresAsync(
        PackageStoreOperationOwner owner,
        ICoordinatedActiveStateTransitionDriver driver,
        ReconciliationCycleContext context,
        IReadOnlyDictionary<string, string> failures,
        CancellationToken cancellationToken)
    {
        if (failures.Count == 0)
            return;

        var applied = context.ApplyResult!;
        var resolution = context.ResolutionResult!;
        var changeSet = context.ChangeSet!;
        var rejected = FindRejectedComponent(
            failures.Keys, PriorState.ActiveGraphsByIdNormalized.Values, applied.SuccessfulGraphSelections);
        var survivingSelections = applied.SuccessfulGraphSelections
            .Where(selection => !selection.Packages.Any(package => rejected.Contains(package.Id))).ToArray();
        var survivingPackages = applied.AppliedPackages.Where(package => !rejected.Contains(package.Id)).ToArray();

        var failedIds = CoordinatedActiveStateTransitionDriver.BuildFailedPackageIds(
            resolution.FailedPackageIds, applied.FailedPackageIds);
        failedIds.UnionWith(rejected);
        var desiredIds = CoordinatedActiveStateTransitionDriver.BuildDesiredRootIds(
            context.DesiredRequests, resolution.ResolvedGraphs, resolution.GraphSelections);

        var merged = new Dictionary<string, string>(context.MergedActive!, StringComparer.OrdinalIgnoreCase);
        foreach (var id in rejected)
            merged.Remove(id);

        // Only the historical closure of roots that remain desired is retained. A concurrently
        // requested removal cannot resurrect an unrelated old root just because an update failed.
        var restored = await driver.RestoreFailedRootSubclosureVersionsAsync(
            owner, merged, desiredIds, failedIds, cancellationToken).ConfigureAwait(false);
        context.MergedActive = new(restored, StringComparer.OrdinalIgnoreCase);

        context.ChangeSet = changeSet with
        {
            Added = changeSet.Added.Where(package => !rejected.Contains(package.Id)).ToArray(),
            Updated = changeSet.Updated.Where(package => !rejected.Contains(package.Id)).ToArray(),
            Removed = PriorState.ActiveVersionById.Keys.Where(id => !restored.ContainsKey(id))
                .Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
        context.ApplyResult = applied with
        {
            AppliedPackages = survivingPackages,
            SuccessfulGraphSelections = survivingSelections,
            FailedPackageIds = applied.FailedPackageIds.Concat(rejected)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }

    internal void ValidatePreparedInstalls(
        StoreStateRecord candidate,
        IReadOnlyList<ResolvedPackageGraphSelection> acceptedSelections)
    {
        var next = candidate.ProtectionRecord ?? throw new InvalidOperationException(
            "Coordinated activation requires a protected publication candidate.");
        var graphs = next.ActiveClosure.Graphs ?? throw new InvalidOperationException(
            "Coordinated activation requires a complete publication closure.");

        foreach (var selection in acceptedSelections)
        {
            var prepared = Handoff.GraphSelections.SingleOrDefault(leased =>
                SameGraph(leased.Graph, selection.Graph));
            var committed = graphs.SingleOrDefault(graph =>
                StringComparer.Ordinal.Equals(graph.GraphId, selection.Graph.GraphId) &&
                StringComparer.Ordinal.Equals(graph.GenerationId, selection.Graph.GenerationId));
            if (prepared is null || committed is null ||
                committed.Nodes.Count != prepared.LeaseOwner.Lease.Snapshot.Nodes.Count)
            {
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                    "The coordinated commit graph does not match a complete pending graph.", next.RootIdentity);
            }

            var lease = prepared.LeaseOwner.Lease;
            var nativeInstalls = committed.Nodes.Select(node => node.Install).ToHashSet();
            foreach (var package in prepared.Packages)
            {
                using var pin = lease.AcquireRead(package.InstallPath);
                if (!nativeInstalls.Remove(lease.GetInstallIdentityForExactPath(package.InstallPath)))
                {
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                        "An install identity changed between coordinated loading and commit.", next.RootIdentity);
                }
            }

            if (nativeInstalls.Count != 0)
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                    "The coordinated commit introduced an install that was not staged.", next.RootIdentity);
        }
    }

    internal static HashSet<string> FindRejectedComponent(
        IEnumerable<string> failedPackageIds,
        IEnumerable<GraphActivationRecord> priorGraphs,
        IEnumerable<ResolvedPackageGraphSelection> candidates)
    {
        var rejected = failedPackageIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = priorGraphs.Where(graph => graph.Status == GraphActivationStatus.Active)
            .Select(graph => graph.NodePackageIds)
            .Concat(candidates.Select(selection => selection.Packages.Select(package => package.Id).ToArray())).ToArray();
        int previousSize;
        do
        {
            previousSize = rejected.Count;
            foreach (var group in groups.Where(rejected.Overlaps))
                rejected.UnionWith(group);
        }
        while (rejected.Count != previousSize);

        return rejected;
    }

    private static bool SameGraph(ResolvedPackageGraph left, ResolvedPackageGraph right) =>
        StringComparer.Ordinal.Equals(left.GraphId, right.GraphId) &&
        StringComparer.Ordinal.Equals(left.GenerationId, right.GenerationId);

    private static PackageStoreAdmissionException Stale(PackageStoreOperationOwner owner, string message) =>
        new(PackageStoreAdmissionReason.StateMismatch, message, owner.Root);

    public async ValueTask DisposeAsync() =>
        await DisposeLeasesAsync(_leases).ConfigureAwait(false);

    private static async ValueTask DisposeLeasesAsync(IReadOnlyList<PackageGraphUseLeaseOwner> leases)
    {
        var failures = new List<Exception>();
        for (var index = leases.Count - 1; index >= 0; index--)
        {
            try
            {
                await leases[index].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
        }

        if (failures.Count != 0)
            throw new AggregateException("Pending package graph leases did not all close cleanly.", failures);
    }
}
