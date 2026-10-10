using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Maintenance;

/// <summary>Builds a bounded read-only inspection snapshot under one owned configured-root operation.</summary>
internal sealed class PackageStoreInspectionService : IPackageStoreInspectionService
{
    private readonly IPhysicalStoreFileSystem _files;
    private readonly IPackageStoreAdmission _admission;
    private readonly IPackageStoreInventory _inventory;
    private readonly IPackageStoreRetentionPlanner _planner;

    internal PackageStoreInspectionService(
        IPhysicalStoreFileSystem files,
        IPackageStoreAdmission admission,
        IPackageStoreInventory inventory,
        IPackageStoreRetentionPlanner planner)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(planner);
        _files = files;
        _admission = admission;
        _inventory = inventory;
        _planner = planner;
    }

    /// <inheritdoc />
    public async Task<PackageStoreInspectionSnapshot> InspectAsync(
        int? keepNewestInactiveVersionsPerPackage = null,
        CancellationToken cancellationToken = default)
    {
        if (keepNewestInactiveVersionsPerPackage < 0)
            throw new ArgumentOutOfRangeException(nameof(keepNewestInactiveVersionsPerPackage));
        cancellationToken.ThrowIfCancellationRequested();

        await using var admission = await _admission.AcquireConfiguredRootOperationAsync(
            PackageStoreAdmissionKind.Maintenance, cancellationToken).ConfigureAwait(false);
        if (admission.Status == PackageStoreAdmissionStatus.Unenrolled)
        {
            return new PackageStoreInspectionSnapshot(
                admission.Status,
                admission.Root,
                enrollmentEpoch: null,
                ledgerDigest: null,
                PackageStoreRetentionProtectionKnowledge.Unknown,
                members: [],
                inventory: null,
                retentionPlan: null,
                uses: [],
                diagnostics: [PackageStoreInspectionDiagnostic.EnrollmentRequired]);
        }

        if (admission.Status != PackageStoreAdmissionStatus.Enrolled)
            throw new ArgumentOutOfRangeException(nameof(admission), "The configured-root admission status is unsupported.");

        var root = admission.Root
            ?? throw Refuse(PackageStoreAdmissionReason.UnknownAuthority, "Enrolled admission did not identify its physical root.");
        var owner = admission.Owner
            ?? throw Refuse(PackageStoreAdmissionReason.UnsupportedParticipant, "Enrolled admission did not retain a root operation owner.", root);
        if (owner.Root != root)
            throw Refuse(PackageStoreAdmissionReason.RootMismatch, "The configured-root admission and owner identify different roots.", root);

        using var borrow = owner.Borrow();
        if (borrow.Root != root || borrow.Epoch != owner.Epoch)
            throw Refuse(PackageStoreAdmissionReason.RootMismatch, "The root borrow changed its admitted identity or epoch.", root);

        var lockedMembers = PackageStoreOperationAccess.GetLockedMemberLocations(borrow);
        lockedMembers.Revalidate();
        var ledger = lockedMembers.Ledger;
        ValidateCompleteBoundary(ledger, root, borrow.Epoch);
        var ledgerDigest = ledger.LedgerDigest;

        var memberSnapshots = new List<PackageStoreInspectionMember>(ledger.Members.Count);
        var protectedInstalls = new List<PackageStoreRetentionProtectedInstall>();
        foreach (var member in ledger.Members.OrderBy(static member => member.MemberId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await lockedMembers.ReadMemberStateAsync(member.MemberId, cancellationToken).ConfigureAwait(false)
                ?? throw Refuse(PackageStoreAdmissionReason.StateMismatch,
                    "A Complete membership member did not return its acknowledged state.", root);

            var graphs = PersistedStoreStateGraphVerifier.VerifyAcknowledgedMember(
                state, ledger, member, root, borrow.Epoch, RootMembershipStatus.Complete);
            var protection = state.ProtectionRecord
                ?? throw Refuse(PackageStoreAdmissionReason.StateMismatch,
                    "An acknowledged member state has no protection record.", root);
            memberSnapshots.Add(new PackageStoreInspectionMember(
                member.MemberId,
                protection.Revision,
                protection.StateBodyDigest,
                protection.ProtectionDigest));

            AddProtectedInstalls(graphs.ActiveGraphs, PackageStoreRetentionProtectionReason.Active, protectedInstalls);
            AddProtectedInstalls(graphs.RecoverableGraphs,
                PackageStoreRetentionProtectionReason.RecoverableLastKnownGood, protectedInstalls);
        }

        lockedMembers.Revalidate();
        RequireSameLedger(lockedMembers, ledgerDigest, root, borrow.Epoch);

        // This callback performs native graph-use inspection only. Member reads and inventory are deliberately outside it;
        // LockedMemberLocations serializes validated-root callbacks and state reads on the same non-reentrant gate.
        var useResult = await PackageStoreOperationAccess.WithValidatedRootAsync(
            borrow,
            (files, heldRoot, token) =>
            {
                if (!ReferenceEquals(files, _files))
                    throw Refuse(PackageStoreAdmissionReason.UnsupportedParticipant,
                        "The inspection service does not share the admitted root's native filesystem provider.", root);
                return new PackageGraphUseRecordStore(files).InspectAsync(heldRoot, root, ledger, token);
            },
            cancellationToken).ConfigureAwait(false);
        if (useResult.RootIdentity != root || useResult.EnrollmentEpoch != borrow.Epoch)
            throw Refuse(PackageStoreAdmissionReason.RootMismatch,
                "Graph-use inventory does not match the admitted root and epoch.", root);

        var uses = useResult.Entries.Select(static entry => new PackageStoreInspectionUse(
            entry.Record.UseId,
            entry.OwnershipState,
            entry.Record.SnapshotState,
            entry.Record.GraphSnapshot)).ToArray();
        foreach (var use in uses.Where(static use => use.OwnershipState == GraphUseRecordOwnershipState.Live))
            AddProtectedInstalls([use.Graph], PackageStoreRetentionProtectionReason.LiveUse, protectedInstalls);

        cancellationToken.ThrowIfCancellationRequested();
        var inventory = await _inventory.ReadAsync(borrow, cancellationToken).ConfigureAwait(false);
        if (inventory.Root != root || inventory.Epoch != borrow.Epoch)
            throw Refuse(PackageStoreAdmissionReason.RootMismatch,
                "Native package inventory does not match the admitted root and epoch.", root);

        lockedMembers.Revalidate();
        RequireSameLedger(lockedMembers, ledgerDigest, root, borrow.Epoch);

        var staleUses = uses.Any(static use => use.OwnershipState == GraphUseRecordOwnershipState.Stale);
        var protectionKnowledge = staleUses
            ? PackageStoreRetentionProtectionKnowledge.Unknown
            : PackageStoreRetentionProtectionKnowledge.Known;
        var completedInstalls = inventory.Entries
            .Where(static entry => entry.Kind == PackageStoreInventoryEntryKind.CompletedInstallCandidate)
            .Select(static entry => entry.InstallIdentity
                ?? throw new InvalidOperationException("A completed inventory candidate omitted its install identity."))
            .ToArray();
        var retentionSnapshot = new PackageStoreRetentionSnapshot(
            root,
            borrow.Epoch,
            inventory.IsComplete ? PackageStoreRetentionInventoryStatus.Complete : PackageStoreRetentionInventoryStatus.Incomplete,
            completedInstalls,
            protectionKnowledge,
            protectedInstalls,
            keepNewestInactiveVersionsPerPackage);
        var retentionPlan = _planner.Plan(retentionSnapshot);

        cancellationToken.ThrowIfCancellationRequested();
        lockedMembers.Revalidate();
        RequireSameLedger(lockedMembers, ledgerDigest, root, borrow.Epoch);

        return new PackageStoreInspectionSnapshot(
            admission.Status,
            root,
            borrow.Epoch,
            ledgerDigest,
            protectionKnowledge,
            memberSnapshots,
            inventory,
            retentionPlan,
            uses,
            staleUses ? [PackageStoreInspectionDiagnostic.StaleGraphUseRequiresRecovery] : []);
    }

    private static void ValidateCompleteBoundary(
        RootMembershipRecord ledger,
        PhysicalRootIdentity root,
        long epoch)
    {
        if (ledger.Status != RootMembershipStatus.Complete || ledger.PendingStateCommit is not null ||
            ledger.RootIdentity != root || ledger.EnrollmentEpoch != epoch || epoch <= 0)
        {
            throw Refuse(PackageStoreAdmissionReason.IncompleteEnrollment,
                "Inspection requires the exact non-pending Complete membership for the admitted root and epoch.", root);
        }
        if (ledger.Members.Count == 0 || ledger.TargetMemberIds.Count != ledger.Members.Count ||
            !ledger.Members.Select(static member => member.MemberId).ToHashSet(StringComparer.Ordinal)
                .SetEquals(ledger.TargetMemberIds))
        {
            throw Refuse(PackageStoreAdmissionReason.IncompleteEnrollment,
                "Inspection requires the exact non-empty Complete member union.", root);
        }
    }

    private static void RequireSameLedger(
        RootMembershipRegistry.LockedMemberLocations lockedMembers,
        string expectedDigest,
        PhysicalRootIdentity root,
        long epoch)
    {
        var current = lockedMembers.Ledger;
        if (current.RootIdentity != root || current.EnrollmentEpoch != epoch ||
            current.Status != RootMembershipStatus.Complete || current.PendingStateCommit is not null ||
            !string.Equals(current.LedgerDigest, expectedDigest, StringComparison.Ordinal))
        {
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "Membership changed during the read-only inspection pass.", root);
        }
    }

    private static void AddProtectedInstalls(
        IEnumerable<ProtectedGraphSnapshot> graphs,
        PackageStoreRetentionProtectionReason reason,
        ICollection<PackageStoreRetentionProtectedInstall> destination)
    {
        foreach (var install in graphs.SelectMany(static graph => graph.Nodes).Select(static node => node.Install))
            destination.Add(new PackageStoreRetentionProtectedInstall(install, reason));
    }

    private static PackageStoreAdmissionException Refuse(
        PackageStoreAdmissionReason reason,
        string message,
        PhysicalRootIdentity? root = null)
        => new(reason, message, root);
}
