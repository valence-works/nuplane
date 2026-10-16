using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using System.Runtime.ExceptionServices;

namespace Nuplane.Store.Maintenance;

/// <summary>Builds a bounded read-only inspection snapshot under one owned configured-root operation.</summary>
internal sealed class PackageStoreInspectionService : IPackageStoreInspectionService
{
    private readonly IPhysicalStoreFileSystem _files;
    private readonly IPackageStoreInventory _inventory;
    private readonly IPackageStoreRetentionPlanner _planner;
    private readonly RootMembershipRegistry _catalogRegistry;
    private readonly ITrustedPackageStoreRootCatalog _rootCatalog;
    private readonly IPackageStoreRootResolver _rootResolver;

    internal PackageStoreInspectionService(
        IPhysicalStoreFileSystem files,
        RootMembershipRegistry catalogRegistry,
        ITrustedPackageStoreRootCatalog rootCatalog,
        IPackageStoreRootResolver rootResolver,
        IPackageStoreInventory inventory,
        IPackageStoreRetentionPlanner planner)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(catalogRegistry);
        ArgumentNullException.ThrowIfNull(rootCatalog);
        ArgumentNullException.ThrowIfNull(rootResolver);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(planner);
        _files = files;
        _inventory = inventory;
        _planner = planner;
        _catalogRegistry = catalogRegistry;
        _rootCatalog = rootCatalog;
        _rootResolver = rootResolver;
    }

    /// <inheritdoc />
    public async Task<PackageStoreInspectionSnapshot> InspectAsync(
        int? keepNewestInactiveVersionsPerPackage = null,
        CancellationToken cancellationToken = default)
    {
        return await InspectRootAsync(_rootResolver.ConfiguredDefaultLabel,
            keepNewestInactiveVersionsPerPackage, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<PackageStoreInspectionSnapshot> InspectRootAsync(
        string? rootLabel,
        int? keepNewestInactiveVersionsPerPackage = null,
        CancellationToken cancellationToken = default)
    {
        if (keepNewestInactiveVersionsPerPackage < 0)
            throw new ArgumentOutOfRangeException(nameof(keepNewestInactiveVersionsPerPackage));
        return await RunUnderCatalogOwnershipAsync(rootLabel,
            (owner, borrow, configuredRoot) => InspectUnderCatalogOwnershipAsync(owner, borrow, configuredRoot,
                keepNewestInactiveVersionsPerPackage, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    internal async Task<TResult> RunUnderCatalogOwnershipAsync<TResult>(
        string? rootLabel,
        Func<RootMembershipRegistry.NativeCatalogOwner, RootMembershipRegistry.NativeCatalogOwnerBorrow,
            TrustedPackageStoreRoot, Task<TResult>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_rootResolver.TryResolve(rootLabel, out var configuredRoot) || configuredRoot is null)
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "The requested package-store root label is not configured.");
        if (!_catalogRegistry.UsesFileSystem(_files))
            throw Refuse(PackageStoreAdmissionReason.UnsupportedParticipant,
                "The inspection service and catalog registry do not share the same native filesystem provider.");

        var owner = await _catalogRegistry.AcquireNativeCatalogOwnerAsync(_rootCatalog, cancellationToken)
            .ConfigureAwait(false);
        RootMembershipRegistry.NativeCatalogOwnerBorrow? borrow = null;
        TResult result = default!;
        Exception? operationFailure = null;
        try
        {
            borrow = await owner.BorrowAsync(cancellationToken).ConfigureAwait(false);
            result = await action(owner, borrow, configuredRoot).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }

        var cleanupFailures = new List<Exception>();
        if (borrow is not null)
        {
            try { await borrow.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
        }
        try { await owner.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { cleanupFailures.Add(exception); }

        if (operationFailure is not null && cleanupFailures.Count > 0)
            throw new AggregateException("Selected-root operation failed and catalog ownership cleanup also failed.",
                [operationFailure, .. cleanupFailures]);
        if (operationFailure is not null)
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        if (cleanupFailures.Count == 1)
            ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
        if (cleanupFailures.Count > 1)
            throw new AggregateException("Catalog ownership cleanup failed after the selected-root operation.", cleanupFailures);

        return result;
    }

    internal async Task<PackageStoreInspectionSnapshot> InspectUnderCatalogOwnershipAsync(
        RootMembershipRegistry.NativeCatalogOwner owner,
        RootMembershipRegistry.NativeCatalogOwnerBorrow borrow,
        TrustedPackageStoreRoot configuredRoot,
        int? keepNewestInactiveVersionsPerPackage,
        CancellationToken cancellationToken,
        RootMembershipRegistry.NativeCatalogOwnerBorrow.Operation? operation = null,
        Func<PackageStoreInventorySnapshot, IReadOnlyList<PackageInstallIdentity>>? executionOnlyInstallOverlay = null)
    {
        if (operation is not null)
            borrow.RequireOperation(operation, _catalogRegistry);
        var selectedRoot = owner.ResolveConfiguredRoot(borrow, configuredRoot);
        var retainedRoots = owner.SnapshotRetainedRoots();
        var verification = retainedRoots.Length == 0
            ? Array.Empty<NativeCatalogMemberVerification>()
            : await VerifyAsync()
                .ConfigureAwait(false);

        if (selectedRoot is null)
        {
            var snapshot = new PackageStoreInspectionSnapshot(
                PackageStoreAdmissionStatus.Unenrolled,
                root: null,
                enrollmentEpoch: null,
                ledgerDigest: null,
                PackageStoreRetentionProtectionKnowledge.Unknown,
                members: [],
                inventory: null,
                retentionPlan: null,
                uses: [],
                diagnostics: [PackageStoreInspectionDiagnostic.EnrollmentRequired]);
            cancellationToken.ThrowIfCancellationRequested();
            owner.RevalidateRetainedEvidence();
            if (verification.Count > 0)
            {
                var peerReplay = await VerifyAsync()
                    .ConfigureAwait(false);
                if (!NativeCatalogMemberVerificationComparer.Same(verification, peerReplay))
                    throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                        "Catalog protection evidence changed during selected-root inspection.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            owner.RevalidateRetainedEvidence();
            return snapshot;
        }

        var root = selectedRoot.RootIdentity;
        var epoch = selectedRoot.EnrollmentEpoch;
        var ledger = selectedRoot.Ledger;
        ValidateCompleteBoundary(ledger, root, epoch);
        var selectedVerification = verification.Where(member => member.RootIdentity == root)
            .OrderBy(static member => member.MemberId, StringComparer.Ordinal).ToArray();
        if (selectedVerification.Length != ledger.Members.Count)
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "Full catalog verification omitted a selected-root member.", root);

        var members = BuildMemberSnapshots(ledger, selectedVerification, root);
        var protectedInstalls = BuildPersistentProtection(selectedVerification, root);
        var rootHandle = selectedRoot.PrimaryResolution.AuthorityRoot
            ?? throw Refuse(PackageStoreAdmissionReason.UnsupportedParticipant,
                "The selected enrolled catalog root has no retained native handle.", root);

        var useResult = await new PackageGraphUseRecordStore(_files)
            .InspectAsync(rootHandle, root, ledger, cancellationToken).ConfigureAwait(false);
        if (useResult.RootIdentity != root || useResult.EnrollmentEpoch != epoch)
            throw Refuse(PackageStoreAdmissionReason.RootMismatch,
                "Graph-use inventory does not match the selected root and epoch.", root);

        var uses = useResult.Entries.Select(static entry => new PackageStoreInspectionUse(
            entry.Record.UseId,
            entry.OwnershipState,
            entry.Record.SnapshotState,
            entry.Record.GraphSnapshot)).ToArray();
        foreach (var use in uses.Where(static use => use.OwnershipState == GraphUseRecordOwnershipState.Live))
            AddProtectedInstalls([use.Graph], PackageStoreRetentionProtectionReason.LiveUse, protectedInstalls, root);

        var catalogInventory = _inventory as IPackageStoreCatalogInventory
            ?? throw Refuse(PackageStoreAdmissionReason.UnsupportedParticipant,
                "The inventory provider cannot read under retained native catalog ownership.", root);
        var inventory = await catalogInventory.ReadUnderCatalogOwnershipAsync(
            rootHandle, root, epoch, cancellationToken).ConfigureAwait(false);
        if (inventory.Root != root || inventory.Epoch != epoch)
            throw Refuse(PackageStoreAdmissionReason.RootMismatch,
                "Native package inventory does not match the selected root and epoch.", root);

        var staleUses = uses.Any(static use => use.OwnershipState == GraphUseRecordOwnershipState.Stale);
        var protectionKnowledge = staleUses
            ? PackageStoreRetentionProtectionKnowledge.Unknown
            : PackageStoreRetentionProtectionKnowledge.Known;
        var completedInstalls = inventory.Entries
            .Where(static entry => entry.Kind == PackageStoreInventoryEntryKind.CompletedInstallCandidate)
            .Select(static entry => entry.InstallIdentity
                ?? throw new InvalidOperationException("A completed inventory candidate omitted its install identity."))
            .ToList();
        if (executionOnlyInstallOverlay is not null)
            completedInstalls.AddRange(executionOnlyInstallOverlay(inventory));
        var retentionSnapshot = new PackageStoreRetentionSnapshot(
            root,
            epoch,
            inventory.IsComplete ? PackageStoreRetentionInventoryStatus.Complete : PackageStoreRetentionInventoryStatus.Incomplete,
            completedInstalls,
            protectionKnowledge,
            protectedInstalls,
            keepNewestInactiveVersionsPerPackage);
        var retentionPlan = _planner.Plan(retentionSnapshot);

        cancellationToken.ThrowIfCancellationRequested();
        owner.RevalidateRetainedEvidence();
        var replay = await VerifyAsync()
            .ConfigureAwait(false);
        if (!NativeCatalogMemberVerificationComparer.Same(verification, replay))
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "Catalog protection evidence changed during selected-root inspection.", root);
        cancellationToken.ThrowIfCancellationRequested();
        owner.RevalidateRetainedEvidence();

        return new PackageStoreInspectionSnapshot(
            PackageStoreAdmissionStatus.Enrolled,
            root,
            epoch,
            ledger.LedgerDigest,
            protectionKnowledge,
            members,
            inventory,
            retentionPlan,
            uses,
            staleUses ? [PackageStoreInspectionDiagnostic.StaleGraphUseRequiresRecovery] : []);

        Task<IReadOnlyList<NativeCatalogMemberVerification>> VerifyAsync()
            => operation is null
                ? _catalogRegistry.VerifyCatalogMemberProtectionAsync(borrow, cancellationToken)
                : _catalogRegistry.VerifyCatalogMemberProtectionUnderOperationAsync(borrow, operation, cancellationToken);
    }

    private static void ValidateCompleteBoundary(
        RootMembershipRecord ledger,
        PhysicalRootIdentity root,
        long epoch)
    {
        if (ledger.Status != RootMembershipStatus.Complete || ledger.PendingStateCommit is not null ||
            ledger.PendingGroupPublicationV2 is not null ||
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

    private static PackageStoreInspectionMember[] BuildMemberSnapshots(
        RootMembershipRecord ledger,
        IReadOnlyList<NativeCatalogMemberVerification> verifiedMembers,
        PhysicalRootIdentity root)
    {
        var members = new List<PackageStoreInspectionMember>(verifiedMembers.Count);
        foreach (var verified in verifiedMembers)
        {
            var member = ledger.Members.SingleOrDefault(candidate => candidate.MemberId == verified.MemberId)
                ?? throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "Full catalog verification returned a member absent from the selected ledger.", root);
            var (revision, stateBodyDigest, protectionDigest) = member.Binding switch
            {
                RootMemberRecord.AcknowledgedBinding acknowledged =>
                    (acknowledged.ProtectionRecord.Revision, acknowledged.ProtectionRecord.StateBodyDigest,
                        acknowledged.ProtectionRecord.ProtectionDigest),
                RootMemberRecord.BundleAcknowledgedBinding bundle =>
                    (bundle.RootRow.Revision, bundle.RootRow.StateBodyDigest, bundle.RootRow.ProtectionDigest),
                _ => throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "Full catalog verification returned an unsupported selected-root binding.", root)
            };
            var expectedStateIdentity = member.Binding switch
            {
                RootMemberRecord.AcknowledgedBinding acknowledged => acknowledged.ObservedStateFileIdentity,
                RootMemberRecord.BundleAcknowledgedBinding bundle => bundle.ObservedStateFileIdentity,
                _ => throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "Full catalog verification returned an unsupported selected-root binding.", root)
            };
            if (revision != verified.Revision || verified.StateFileIdentity != expectedStateIdentity)
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "Selected-root acknowledgement metadata changed during verification.", root);
            members.Add(new PackageStoreInspectionMember(member.MemberId, revision, stateBodyDigest, protectionDigest));
        }
        return members.ToArray();
    }

    private static List<PackageStoreRetentionProtectedInstall> BuildPersistentProtection(
        IEnumerable<NativeCatalogMemberVerification> verifiedMembers,
        PhysicalRootIdentity root)
    {
        var protectedInstalls = new List<PackageStoreRetentionProtectedInstall>();
        foreach (var member in verifiedMembers)
        {
            if (member.V1Graphs is { } v1)
            {
                AddProtectedInstalls(v1.ActiveGraphs, PackageStoreRetentionProtectionReason.Active, protectedInstalls, root);
                AddProtectedInstalls(v1.RecoverableGraphs,
                    PackageStoreRetentionProtectionReason.RecoverableLastKnownGood, protectedInstalls, root);
            }
            if (member.V2Graphs is { } v2)
            {
                AddProtectedInstalls(v2.ActiveGraphs, PackageStoreRetentionProtectionReason.Active, protectedInstalls, root);
                AddProtectedInstalls(v2.RecoverableGraphs,
                    PackageStoreRetentionProtectionReason.RecoverableLastKnownGood, protectedInstalls, root);
            }
        }
        return protectedInstalls;
    }

    private static void AddProtectedInstalls(
        IEnumerable<ProtectedGraphSnapshotV2> graphs,
        PackageStoreRetentionProtectionReason reason,
        ICollection<PackageStoreRetentionProtectedInstall> destination,
        PhysicalRootIdentity root)
    {
        foreach (var install in graphs.SelectMany(static graph => graph.Nodes).Select(static node => node.Install)
                     .Where(install => install.Root == root))
            destination.Add(new PackageStoreRetentionProtectedInstall(install, reason));
    }

    private static void AddProtectedInstalls(
        IEnumerable<ProtectedGraphSnapshot> graphs,
        PackageStoreRetentionProtectionReason reason,
        ICollection<PackageStoreRetentionProtectedInstall> destination,
        PhysicalRootIdentity root)
    {
        foreach (var install in graphs.SelectMany(static graph => graph.Nodes).Select(static node => node.Install)
                     .Where(install => install.Root == root))
            destination.Add(new PackageStoreRetentionProtectedInstall(install, reason));
    }

    private static PackageStoreAdmissionException Refuse(
        PackageStoreAdmissionReason reason,
        string message,
        PhysicalRootIdentity? root = null)
        => new(reason, message, root);
}
