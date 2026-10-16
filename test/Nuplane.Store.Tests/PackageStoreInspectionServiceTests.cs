using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.Maintenance;
using Nuplane.Store.State;
using Nuplane.Store.Tests.Coordination;
using Nuplane.Tests.Shared;
using Microsoft.Extensions.Options;

namespace Nuplane.Store.Tests;

public sealed class PackageStoreInspectionServiceTests
{
    [SupportedPhysicalStoreFact]
    public async Task InspectAsync_CompleteTwoStateRootAndAbsentPolicy_ReportsEveryInstallRetainedWithoutChangingPayloads()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var inactivePath = AddInstall(context.Fixture, "feed-c", "Unused.Widget", "1.0.0");
        var inactiveNuspec = Path.Combine(inactivePath, "Unused.Widget.nuspec");
        var originalPayload = await File.ReadAllBytesAsync(inactiveNuspec);

        var snapshot = await CreateService(context).InspectAsync();

        Assert.Equal(PackageStoreAdmissionStatus.Enrolled, snapshot.AdmissionStatus);
        Assert.Equal(context.RootIdentity, snapshot.Root);
        Assert.Equal(1, snapshot.EnrollmentEpoch);
        Assert.Equal(context.Registry.ReadCandidate(context.Root).LedgerDigest, snapshot.LedgerDigest);
        Assert.Equal(new[] { "first", "second" }, snapshot.Members.Select(static member => member.MemberId));
        Assert.All(snapshot.Members, static member =>
        {
            Assert.Equal(1, member.Revision);
            Assert.Equal(64, member.StateBodyDigest.Length);
            Assert.Equal(64, member.ProtectionDigest.Length);
        });
        Assert.NotNull(snapshot.Inventory);
        Assert.True(snapshot.Inventory!.IsComplete);
        Assert.NotNull(snapshot.RetentionPlan);
        Assert.Equal(PackageStoreRetentionProtectionKnowledge.Known, snapshot.ProtectionKnowledge);
        Assert.All(snapshot.RetentionPlan!.Entries,
            static entry => Assert.Equal(PackageStoreRetentionClassification.Retained, entry.Classification));
        Assert.Contains(snapshot.RetentionPlan.Entries, entry =>
            entry.Install.RootRelativeInstallPath == "feed-c/Unused.Widget/1.0.0" &&
            entry.Reasons.Contains(PackageStoreRetentionReason.RetentionPolicyAbsent));
        Assert.Equal(originalPayload, await File.ReadAllBytesAsync(inactiveNuspec));
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectAsync_ZeroBudget_OnlyMarksUnprotectedInstallEligible()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var inactivePath = AddInstall(context.Fixture, "feed-c", "Unused.Widget", "1.0.0");
        var inactiveBytes = await File.ReadAllBytesAsync(Path.Combine(inactivePath, "Unused.Widget.nuspec"));

        var snapshot = await CreateService(context).InspectAsync(keepNewestInactiveVersionsPerPackage: 0);

        var plan = Assert.IsType<PackageStoreRetentionPlan>(snapshot.RetentionPlan);
        var inactive = Assert.Single(plan.Entries, static entry =>
            entry.Install.RootRelativeInstallPath == "feed-c/Unused.Widget/1.0.0");
        Assert.Equal(PackageStoreRetentionClassification.Eligible, inactive.Classification);
        Assert.Contains(PackageStoreRetentionReason.OutsideInactiveRetentionBudget, inactive.Reasons);
        foreach (var state in context.States.Values)
        {
            foreach (var graph in state.ProtectionRecord!.ActiveClosure.Graphs!)
            {
                foreach (var install in graph.Nodes.Select(static node => node.Install))
                {
                    var entry = Assert.Single(plan.Entries, candidate => candidate.Install == install);
                    Assert.Equal(PackageStoreRetentionClassification.Retained, entry.Classification);
                    Assert.Contains(PackageStoreRetentionReason.ProtectedActive, entry.Reasons);
                }
            }
            foreach (var graph in state.ProtectionRecord.RecoverableClosure.Graphs!)
            {
                foreach (var install in graph.Nodes.Select(static node => node.Install))
                {
                    var entry = Assert.Single(plan.Entries, candidate => candidate.Install == install);
                    Assert.Equal(PackageStoreRetentionClassification.Retained, entry.Classification);
                    Assert.Contains(PackageStoreRetentionReason.ProtectedRecoverableLastKnownGood, entry.Reasons);
                }
            }
        }
        Assert.DoesNotContain(plan.Entries, static entry =>
            entry.Classification == PackageStoreRetentionClassification.Eligible &&
            entry.Reasons.Any(static reason => reason is PackageStoreRetentionReason.ProtectedActive or
                PackageStoreRetentionReason.ProtectedRecoverableLastKnownGood or PackageStoreRetentionReason.ProtectedLiveUse));
        Assert.Equal(inactiveBytes, await File.ReadAllBytesAsync(Path.Combine(inactivePath, "Unused.Widget.nuspec")));
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectAsync_LiveUseProtectsItsExactGraphAndReportsItsOwnership()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        await using var observer = new PackageGraphUseLifetimeObserver();
        var admission = await CreateAdmission(context).AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
        var borrow = Assert.IsType<PackageStoreOperationOwner>(admission.Owner).Borrow();
        var leaseOwner = await new PackageGraphUseLeaseAcquisition(context.Registry, observer).AcquireForRootAsync(
            borrow,
            context.Graphs["first"],
            context.Requests["first"],
            PackageGraphUseSnapshotState.Committed);
        borrow.Dispose();
        await admission.DisposeAsync();

        try
        {
            var snapshot = await CreateService(context).InspectAsync(keepNewestInactiveVersionsPerPackage: 0);

            var use = Assert.Single(snapshot.Uses);
            Assert.Equal(GraphUseRecordOwnershipState.Live, use.OwnershipState);
            Assert.Equal(PackageGraphUseSnapshotState.Committed, use.SnapshotState);
            Assert.Equal(context.Graphs["first"].Nodes.Count, use.Graph.Nodes.Count);
            var plan = Assert.IsType<PackageStoreRetentionPlan>(snapshot.RetentionPlan);
            foreach (var install in use.Graph.Nodes.Select(static node => node.Install))
            {
                var entry = Assert.Single(plan.Entries, candidate => candidate.Install == install);
                Assert.Equal(PackageStoreRetentionClassification.Retained, entry.Classification);
                Assert.Contains(PackageStoreRetentionReason.ProtectedLiveUse, entry.Reasons);
            }
        }
        finally
        {
            await leaseOwner.DisposeAsync();
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectAsync_StaleUseMakesProtectionUnknownButKeepsPersistentPositiveProtection()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var inactivePath = AddInstall(context.Fixture, "feed-c", "Unused.Widget", "1.0.0");
        await using var observer = new PackageGraphUseLifetimeObserver();
        var admission = await CreateAdmission(context).AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
        var borrow = Assert.IsType<PackageStoreOperationOwner>(admission.Owner).Borrow();
        var leaseOwner = await new PackageGraphUseLeaseAcquisition(context.Registry, observer).AcquireForRootAsync(
            borrow,
            context.Graphs["first"],
            context.Requests["first"],
            PackageGraphUseSnapshotState.Pending);
        borrow.Dispose();
        await admission.DisposeAsync();
        await leaseOwner.DisposeAsync();

        var snapshot = await CreateService(context).InspectAsync(keepNewestInactiveVersionsPerPackage: 0);

        Assert.True(snapshot.HasStaleUses);
        Assert.Contains(PackageStoreInspectionDiagnostic.StaleGraphUseRequiresRecovery, snapshot.Diagnostics);
        Assert.Equal(PackageStoreRetentionProtectionKnowledge.Unknown, snapshot.ProtectionKnowledge);
        Assert.Equal(GraphUseRecordOwnershipState.Stale, Assert.Single(snapshot.Uses).OwnershipState);
        var plan = Assert.IsType<PackageStoreRetentionPlan>(snapshot.RetentionPlan);
        Assert.Contains(PackageStoreRetentionReason.ProtectionUnknown, plan.Reasons);
        var active = Assert.Single(plan.Entries, static entry => entry.Install.PackageId == "Root.First");
        Assert.Equal(PackageStoreRetentionClassification.Retained, active.Classification);
        Assert.Contains(PackageStoreRetentionReason.ProtectedActive, active.Reasons);
        var inactive = Assert.Single(plan.Entries, entry =>
            entry.Install.RootRelativeInstallPath == ToRootRelative(context.Fixture.PackageInstallRoot, inactivePath));
        Assert.Equal(PackageStoreRetentionClassification.Refused, inactive.Classification);
        Assert.DoesNotContain(plan.Entries, static entry => entry.Classification == PackageStoreRetentionClassification.Eligible);
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectAsync_KnownEmptyProtectionIsDistinctFromUnknownAndCanPlanUnusedInstall()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateAsync();
        foreach (var memberId in context.States.Keys.ToArray())
            context.States[memberId] = CreateKnownEmptyState(context, memberId);
        await context.PublishStatesAsync();
        await context.Registry.CompleteEnrollmentAsync(context.Root, context.RootIdentity, 1, true, CancellationToken.None);
        var inactivePath = AddInstall(context.Fixture, "feed-c", "Unused.Widget", "1.0.0");

        var snapshot = await CreateService(context).InspectAsync(keepNewestInactiveVersionsPerPackage: 0);

        Assert.Equal(PackageStoreRetentionProtectionKnowledge.Known, snapshot.ProtectionKnowledge);
        Assert.Empty(snapshot.Uses);
        var plan = Assert.IsType<PackageStoreRetentionPlan>(snapshot.RetentionPlan);
        Assert.Equal(PackageStoreRetentionClassification.Eligible,
            Assert.Single(plan.Entries, entry => entry.Install.RootRelativeInstallPath ==
                ToRootRelative(context.Fixture.PackageInstallRoot, inactivePath)).Classification);
        Assert.DoesNotContain(PackageStoreRetentionReason.ProtectionUnknown, plan.Reasons);
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectAsync_IncompleteInventoryRefusesEveryUnprotectedCandidate()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var inactivePath = AddInstall(context.Fixture, "feed-c", "Unused.Widget", "1.0.0");
        var relativePath = ToRootRelative(context.Fixture.PackageInstallRoot, inactivePath);
        using var identityObservation = new PackageInstallIdentityReader(context.Files).Observe(
            context.Root, context.RootIdentity, relativePath, "Unused.Widget", "1.0.0");
        var incompleteInventory = new FixedInventory(new PackageStoreInventorySnapshot(
            context.RootIdentity,
            1,
            isComplete: false,
            entries: [new PackageStoreInventoryEntry(relativePath, PackageStoreInventoryEntryKind.CompletedInstallCandidate,
                installIdentity: identityObservation.InstallIdentity)],
            issues: [new PackageStoreInventoryIssue(relativePath, "Injected bounded-observation uncertainty.")]));

        var snapshot = await CreateService(context, incompleteInventory).InspectAsync(keepNewestInactiveVersionsPerPackage: 0);

        Assert.False(snapshot.Inventory!.IsComplete);
        var plan = Assert.IsType<PackageStoreRetentionPlan>(snapshot.RetentionPlan);
        Assert.Contains(PackageStoreRetentionReason.InventoryIncomplete, plan.Reasons);
        var candidate = Assert.Single(plan.Entries);
        Assert.Equal(PackageStoreRetentionClassification.Refused, candidate.Classification);
        Assert.DoesNotContain(plan.Entries, static entry => entry.Classification == PackageStoreRetentionClassification.Eligible);
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectAsync_UnenrolledRootReturnsUnknownWithoutInventoryOrPlan()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateFileSystem();
        using var root = PhysicalStoreTestDirectory.Open(files, fixture.PackageInstallRoot);
        var catalog = new TestCatalog([new TrustedPackageStoreRoot("default", fixture.PackageInstallRoot)]);
        var resolver = new PackageStoreRootResolver(catalog, Options.Create(new PackageStoreMaintenanceOptions()));
        var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
        var inventory = new CountingInventory();
        var service = new PackageStoreInspectionService(files, registry, catalog, resolver,
            inventory, new PackageStoreRetentionPlanner());

        var snapshot = await service.InspectAsync(keepNewestInactiveVersionsPerPackage: 0);

        Assert.Equal(PackageStoreAdmissionStatus.Unenrolled, snapshot.AdmissionStatus);
        Assert.Null(snapshot.EnrollmentEpoch);
        Assert.Null(snapshot.LedgerDigest);
        Assert.Equal(PackageStoreRetentionProtectionKnowledge.Unknown, snapshot.ProtectionKnowledge);
        Assert.Contains(PackageStoreInspectionDiagnostic.EnrollmentRequired, snapshot.Diagnostics);
        Assert.Null(snapshot.Inventory);
        Assert.Null(snapshot.RetentionPlan);
        Assert.Empty(snapshot.Members);
        Assert.Empty(snapshot.Uses);
        Assert.Equal(0, inventory.ReadCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectRootAsync_AdditionalConfiguredLabelInspectsOnlySelectedRoot()
    {
        using var defaultRoot = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        using var additionalRoot = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var roots = new TestCatalog([
            new TrustedPackageStoreRoot("default", defaultRoot.Fixture.PackageInstallRoot),
            new TrustedPackageStoreRoot("additional", additionalRoot.Fixture.PackageInstallRoot)
        ]);
        var resolver = new PackageStoreRootResolver(roots, Options.Create(new PackageStoreMaintenanceOptions()));
        var service = new PackageStoreInspectionService(defaultRoot.Files, defaultRoot.Registry, roots, resolver,
            new PackageStoreInventory(defaultRoot.Files), new PackageStoreRetentionPlanner());

        var snapshot = await service.InspectRootAsync("additional", keepNewestInactiveVersionsPerPackage: 0);

        Assert.Equal(PackageStoreAdmissionStatus.Enrolled, snapshot.AdmissionStatus);
        Assert.Equal(additionalRoot.RootIdentity, snapshot.Root);
        Assert.Equal(new[] { "first", "second" }, snapshot.Members.Select(static member => member.MemberId));
        Assert.NotEmpty(Assert.IsType<PackageStoreRetentionPlan>(snapshot.RetentionPlan).Entries
            .Where(static entry => entry.Reasons.Contains(PackageStoreRetentionReason.ProtectedActive)));
        Assert.All(snapshot.RetentionPlan!.Entries.Where(static entry =>
            entry.Reasons.Contains(PackageStoreRetentionReason.ProtectedActive) ||
            entry.Reasons.Contains(PackageStoreRetentionReason.ProtectedRecoverableLastKnownGood)),
            entry => Assert.Equal(additionalRoot.RootIdentity, entry.Install.Root));
    }

    [Fact]
    public async Task InspectRootAsync_UnknownLabelRefusesBeforeNativeAccess()
    {
        var files = CreateFileSystem();
        var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
        var catalog = new TestCatalog([new TrustedPackageStoreRoot("configured", "\0invalid-native-path")]);
        var resolver = new PackageStoreRootResolver(catalog, Options.Create(new PackageStoreMaintenanceOptions()));
        var service = new PackageStoreInspectionService(files, registry, catalog, resolver,
            new CountingInventory(), new PackageStoreRetentionPlanner());

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => service.InspectRootAsync("missing"));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectRootAsync_RefusesMismatchedNativeProviderBeforeCatalogAccess()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var catalog = new TestCatalog([new TrustedPackageStoreRoot("default", context.Fixture.PackageInstallRoot)]);
        var resolver = new PackageStoreRootResolver(catalog, Options.Create(new PackageStoreMaintenanceOptions()));
        var inventory = new CountingInventory();
        var service = new PackageStoreInspectionService(CreateFileSystem(), context.Registry,
            catalog, resolver, inventory, new PackageStoreRetentionPlanner());

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => service.InspectAsync());

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, refusal.Reason);
        Assert.Equal(0, inventory.ReadCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectRootAsync_UnenrolledSelectedRootStillVerifiesEnrolledPeer()
    {
        using var peer = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        using var fixture = new PackageStoreFixture();
        var catalog = new TestCatalog([
            new TrustedPackageStoreRoot("default", fixture.PackageInstallRoot),
            new TrustedPackageStoreRoot("peer", peer.Fixture.PackageInstallRoot)
        ]);
        var resolver = new PackageStoreRootResolver(catalog, Options.Create(new PackageStoreMaintenanceOptions()));
        var inventory = new CountingInventory();
        var service = new PackageStoreInspectionService(peer.Files, peer.Registry, catalog, resolver,
            inventory, new PackageStoreRetentionPlanner());

        var snapshot = await service.InspectRootAsync("default");

        Assert.Equal(PackageStoreAdmissionStatus.Unenrolled, snapshot.AdmissionStatus);
        Assert.Null(snapshot.Root);
        Assert.Equal(PackageStoreRetentionProtectionKnowledge.Unknown, snapshot.ProtectionKnowledge);
        Assert.Null(snapshot.Inventory);
        Assert.Null(snapshot.RetentionPlan);
        Assert.Equal(0, inventory.ReadCount);
    }

    [SupportedPhysicalStoreTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InspectRootAsync_PendingMemberStateOnSelectedOrPeerRefusesWithoutRecovery(bool pendingSelected)
    {
        using var selected = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        using var peer = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var pendingContext = pendingSelected ? selected : peer;
        var parents = pendingContext.StatePaths.ToDictionary(
            static pair => pair.Key,
            pair => PhysicalStoreTestDirectory.Open(pendingContext.Files, Path.GetDirectoryName(pair.Value)!),
            StringComparer.Ordinal);
        try
        {
            await Assert.ThrowsAsync<InspectionInterruptedException>(() => pendingContext.Registry.PublishStateAsync(
                pendingContext.Root,
                parents,
                "first",
                pendingContext.Reprotect(pendingContext.States["first"], legacyUnknown: false, revision: 2),
                CancellationToken.None,
                point =>
                {
                    if (point == RootMembershipPublicationPoint.PendingPublished)
                        throw new InspectionInterruptedException();
                }));
        }
        finally
        {
            foreach (var parent in parents.Values.Reverse())
                parent.Dispose();
        }

        var pending = pendingContext.Registry.ReadCandidate(pendingContext.Root).PendingStateCommit;
        Assert.NotNull(pending);
        var before = CaptureMembershipEvidence(selected) + CaptureMembershipEvidence(peer);
        var roots = new TestCatalog([
            new TrustedPackageStoreRoot("default", selected.Fixture.PackageInstallRoot),
            new TrustedPackageStoreRoot("peer", peer.Fixture.PackageInstallRoot)
        ]);
        var resolver = new PackageStoreRootResolver(roots, Options.Create(new PackageStoreMaintenanceOptions()));
        var service = new PackageStoreInspectionService(selected.Files, selected.Registry, roots, resolver,
            new PackageStoreInventory(selected.Files), new PackageStoreRetentionPlanner());

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            service.InspectRootAsync("default", cancellationToken: deadline.Token));

        Assert.Contains(refusal.Reason, new[] { PackageStoreAdmissionReason.IncompleteEnrollment,
            PackageStoreAdmissionReason.UnknownAuthority, PackageStoreAdmissionReason.StateMismatch });
        Assert.NotNull(pendingContext.Registry.ReadCandidate(pendingContext.Root).PendingStateCommit);
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            service.InspectRootAsync("default", cancellationToken: deadline.Token));
        Assert.Equal(before, CaptureMembershipEvidence(selected) + CaptureMembershipEvidence(peer));
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectAsync_IncompleteMembershipRefusesBeforeInventory()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateAsync();
        var inventory = new CountingInventory();
        var service = CreateService(context, inventory);

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => service.InspectAsync());

        Assert.Equal(PackageStoreAdmissionReason.IncompleteEnrollment, refusal.Reason);
        Assert.Equal(0, inventory.ReadCount);
    }

    [Fact]
    public async Task InspectAsync_NegativeBudgetAndPreCancelledCall_RefuseBeforeAdmission()
    {
        var files = CreateFileSystem();
        var resolver = new CountingRootResolver();
        var service = new PackageStoreInspectionService(files,
            new RootMembershipRegistry(files, new StoreStateSerializer()), new TestCatalog([]), resolver,
            new CountingInventory(), new PackageStoreRetentionPlanner());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.InspectAsync(-1));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InspectAsync(cancellationToken: cancelled.Token));
        Assert.Equal(0, resolver.ResolveCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task Snapshot_CopiesMemberAndUseCollectionsAtConstruction()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var root = context.RootIdentity;
        var inventory = new PackageStoreInventorySnapshot(root, 1, true, [], []);
        var plan = new PackageStoreRetentionPlanner().Plan(new PackageStoreRetentionSnapshot(
            root, 1, PackageStoreRetentionInventoryStatus.Complete, [], PackageStoreRetentionProtectionKnowledge.Known,
            [], keepNewestInactiveVersionsPerPackage: null));
        var members = new List<PackageStoreInspectionMember>
        {
            new("member-a", 1, new string('a', 64), new string('b', 64))
        };
        var graph = Assert.Single(context.States["first"].ProtectionRecord!.ActiveClosure.Graphs!);
        var activeOnlyGraph = new ProtectedGraphSnapshot(Guid.NewGuid(), graph.GraphId, graph.GenerationId,
            ProtectedGraphDisposition.Active, graph.Roots, graph.RequestedRoots, graph.Nodes, graph.Edges,
            recoverySelectionEvidence: null);
        var uses = new List<PackageStoreInspectionUse>
        {
            new(Guid.NewGuid(), GraphUseRecordOwnershipState.Stale, PackageGraphUseSnapshotState.Committed, activeOnlyGraph)
        };
        var snapshot = new PackageStoreInspectionSnapshot(PackageStoreAdmissionStatus.Enrolled, root, 1,
            new string('c', 64), PackageStoreRetentionProtectionKnowledge.Known, members, inventory, plan, uses, []);

        members.Add(new PackageStoreInspectionMember("member-b", 2, new string('d', 64), new string('e', 64)));
        uses.Clear();
        Assert.Single(snapshot.Members);
        Assert.Single(snapshot.Uses);
        Assert.Throws<NotSupportedException>(() => ((IList<PackageStoreInspectionMember>)snapshot.Members).Add(
            new PackageStoreInspectionMember("member-c", 3, new string('f', 64), new string('a', 64))));
        Assert.Throws<NotSupportedException>(() => ((IList<PackageStoreInspectionUse>)snapshot.Uses).Clear());
    }

    private static PackageStoreInspectionService CreateService(
        RootMembershipProtectionVerificationTests.Context context,
        IPackageStoreInventory? inventory = null,
        IReadOnlyList<TrustedPackageStoreRoot>? configuredRoots = null)
    {
        var catalog = new TestCatalog(configuredRoots ?? [new TrustedPackageStoreRoot("default", context.Fixture.PackageInstallRoot)]);
        var resolver = new PackageStoreRootResolver(catalog, Options.Create(new PackageStoreMaintenanceOptions()));
        return new(context.Files, context.Registry, catalog, resolver,
            inventory ?? new PackageStoreInventory(context.Files), new PackageStoreRetentionPlanner());
    }

    private static PackageStoreAdmission CreateAdmission(RootMembershipProtectionVerificationTests.Context context)
        => new(context.Files, context.Registry, context.Fixture.PackageInstallRoot);

    private static IPhysicalStoreFileSystem CreateFileSystem()
        => OperatingSystem.IsWindows() ? new WindowsPhysicalStoreFileSystem() : new UnixPhysicalStoreFileSystem();

    private static string AddInstall(PackageStoreFixture fixture, string feed, string packageId, string version)
    {
        var path = fixture.CreateDirectory($"packages/{feed}/{packageId}/{version}");
        File.WriteAllText(Path.Combine(path, packageId + ".nuspec"),
            $"<package><metadata><id>{packageId}</id><version>{version}</version></metadata></package>");
        File.WriteAllBytes(Path.Combine(path, PackageInstallStore.CompletionMarkerFileName), []);
        return path;
    }

    private static StoreStateRecord CreateKnownEmptyState(
        RootMembershipProtectionVerificationTests.Context context,
        string memberId)
    {
        var state = StoreStateRecord.Empty() with { UpdatedAt = DateTimeOffset.UnixEpoch };
        var closure = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, []);
        var candidate = new PackageProtectionRecord(1, context.RootIdentity, 1, memberId, 1,
            ProtectionDigest.StateBody(state), new string('0', 64), closure, closure, [], false);
        var protection = new PackageProtectionRecord(1, context.RootIdentity, 1, memberId, 1,
            candidate.StateBodyDigest, ProtectionDigest.Protection(candidate), closure, closure, [], false);
        return state with { ProtectionRecord = protection };
    }

    private static string ToRootRelative(string root, string path)
        => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private sealed class CountingInventory : IPackageStoreInventory, IPackageStoreCatalogInventory
    {
        internal int ReadCount { get; private set; }

        public Task<PackageStoreInventorySnapshot> ReadAsync(
            PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            throw new InvalidOperationException("This test inventory should not be read.");
        }

        public Task<PackageStoreInventorySnapshot> ReadUnderCatalogOwnershipAsync(
            PhysicalStoreDirectoryHandle heldRoot,
            PhysicalRootIdentity expectedRoot,
            long epoch,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            throw new InvalidOperationException("This test inventory should not be read.");
        }
    }

    private sealed class FixedInventory(PackageStoreInventorySnapshot snapshot)
        : IPackageStoreInventory, IPackageStoreCatalogInventory
    {
        public Task<PackageStoreInventorySnapshot> ReadAsync(
            PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.Root != borrow.Root || snapshot.Epoch != borrow.Epoch)
                throw new InvalidOperationException("The fixed test inventory must match the current borrow.");
            return Task.FromResult(snapshot);
        }

        public Task<PackageStoreInventorySnapshot> ReadUnderCatalogOwnershipAsync(
            PhysicalStoreDirectoryHandle heldRoot,
            PhysicalRootIdentity expectedRoot,
            long epoch,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.Root != expectedRoot || snapshot.Epoch != epoch)
                throw new InvalidOperationException("The fixed inventory must match the current catalog selection.");
            return Task.FromResult(snapshot);
        }
    }

    private sealed class CountingRootResolver : IPackageStoreRootResolver
    {
        internal int ResolveCount { get; private set; }
        public string? ConfiguredDefaultLabel => "default";

        public bool TryResolve(string? label, out TrustedPackageStoreRoot? root)
        {
            ResolveCount++;
            root = null;
            return false;
        }
    }

    private sealed class TestCatalog(IReadOnlyList<TrustedPackageStoreRoot> roots) : ITrustedPackageStoreRootCatalog
    {
        public IReadOnlyList<TrustedPackageStoreRoot> Roots { get; } = Array.AsReadOnly(roots.ToArray());
    }

    private static string CaptureMembershipEvidence(RootMembershipProtectionVerificationTests.Context context)
    {
        var ledger = context.Registry.ReadCandidate(context.Root);
        var pending = ledger.PendingStateCommit;
        var pendingMember = pending is null ? null : ledger.Members.Single(member => member.MemberId == pending.MemberId);
        var artifactDirectory = pendingMember is null ? null : Path.GetDirectoryName(pendingMember.ConfiguredLocator);
        var artifacts = pending is null
            ? Array.Empty<string>()
            : new[]
            {
                Path.Combine(artifactDirectory!, RootMembershipRegistry.StageName(pending)),
                Path.Combine(artifactDirectory!, RootMembershipRegistry.BackupName(pending))
            };
        var paths = ledger.Members.Select(static member => member.ConfiguredLocator)
            .Append(Path.Combine(context.Fixture.PackageInstallRoot, RootMembershipRegistry.ControlDirectoryName,
                RootMembershipRegistry.LedgerName))
            .Concat(artifacts);
        return PackageStoreInspectionEvidenceCapture.Capture(context.Files,
            [context.Fixture.PackageInstallRoot], paths);
    }

    private sealed class InspectionInterruptedException : Exception { }
}
