using Microsoft.Extensions.Options;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.DeletionRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Maintenance;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

public sealed partial class RootMembershipNativeGroupPublicationTests
{
    [SupportedPhysicalStoreFact]
    public async Task ExecutionPlanning_RetainsFullCatalogUnionThroughReadsInventoryPlanAndCandidate()
    {
        using var fixture = await ExecutionFixture.CreateAsync(async context =>
        {
            var peer = await context.AddIndependentCompleteV1RootAsync();
            return [peer.PackageInstallRoot];
        });
        PackageStoreExecutionService.Context? escaped = null;
        PhysicalStoreDirectoryHandle? capturedRoot = null;
        var result = await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, _) =>
        {
            fixture.AssertAllLocksHeld();
            escaped = scope;
            capturedRoot = scope.RootHandle;
            Assert.Equal(fixture.Context.RootAIdentity.HandleIdentity, fixture.Context.Files.InspectHandle(scope.RootHandle).Identity);
            AssertLocalProtection(scope.Snapshot, fixture.Context.RootAIdentity, fixture.Context.NextState);
            Assert.Equal(0, scope.Snapshot.RetentionPlan!.KeepNewestInactiveVersionsPerPackage);
            return Task.FromResult(42);
        });

        Assert.Equal(42, result);
        // Confirmed execution verifies all four catalog state slots before and after stale-use recovery.
        // The serializer checks the complete retained lock union on every one of these reads.
        Assert.Equal(20, fixture.Serializer.ReadCalls);
        Assert.Equal(1, fixture.Inventory.ReadCalls);
        Assert.Equal(1, fixture.Planner.PlanCalls);
        Assert.Throws<PackageStoreAdmissionException>(() => escaped!.Revalidate(CancellationToken.None));
        Assert.Throws<PackageStoreAdmissionException>(() => escaped!.RootHandle);
        var expired = Assert.Throws<PackageStoreAdmissionException>(() => fixture.Context.Files.InspectHandle(capturedRoot!));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, expired.Reason);
        AssertExecutionLocksReleased(fixture.Context, fixture.AdditionalRoots);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecutionPlanning_ReplansRealInventoryAfterEarlierPreview()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var preview = await fixture.Inspection.InspectRootAsync("root-0", 0);
        var install = Path.Combine(fixture.Context.RootAPath, "feed", "inactive", "1.0.0");
        Directory.CreateDirectory(install);
        File.WriteAllBytes(Path.Combine(install, PackageInstallStore.CompletionMarkerFileName), []);

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, _) =>
        {
            var candidate = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive");
            Assert.Equal(PackageStoreRetentionClassification.Eligible, candidate.Classification);
            Assert.DoesNotContain(preview.RetentionPlan!.Entries, entry => entry.Install.PackageId == "inactive");
            return Task.FromResult(true);
        });

        Assert.True(Directory.Exists(install));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecutionPlanning_RereadsNewAcknowledgedStateAfterEarlierPreview()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var preview = await fixture.Inspection.InspectRootAsync("root-0", 0);
        var next = fixture.Context.PrepareNextGraphPublication();
        await fixture.Context.Registry().PublishNativeGroupAsync(next.Descriptor, fixture.Context.Requests,
            next.NextState, CancellationToken.None);

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, _) =>
        {
            var previous = Assert.Single(preview.Members, member => member.MemberId == "shared-a");
            var current = Assert.Single(scope.Snapshot.Members, member => member.MemberId == "shared-a");
            Assert.Equal(previous.Revision + 1, current.Revision);
            Assert.NotEqual(previous.StateBodyDigest, current.StateBodyDigest);
            return Task.FromResult(true);
        });

        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecutionPlanning_UnenrolledConfiguredRootRefusesCandidate()
    {
        using var fixture = await ExecutionFixture.CreateAsync(context =>
            Task.FromResult(new[] { context.CreateUnboundRootPath() }));
        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            fixture.Service.PlanAndRunAsync("root-2", 1, 0, fixture.UnexpectedCandidate));

        Assert.Equal(PackageStoreAdmissionReason.IncompleteEnrollment, refusal.Reason);
        fixture.AssertNoPlanning();
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecutionPlanning_ChangedEpochRefusesBeforePayloadInventoryAndCandidate()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            fixture.Service.PlanAndRunAsync("root-0", 2, 0, fixture.UnexpectedCandidate));

        Assert.Equal(PackageStoreAdmissionReason.RootMismatch, refusal.Reason);
        fixture.AssertNoPlanning();
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecutionPlanning_UnknownLabelRefusesBeforePayloadInventoryAndCandidate()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            fixture.Service.PlanAndRunAsync("not-configured", 1, 0, fixture.UnexpectedCandidate));

        fixture.AssertNoPlanning();
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecutionPlanning_CorruptIndependentPeerRefusesBeforeInventoryAndCandidate()
    {
        using var fixture = await ExecutionFixture.CreateAsync(async context =>
        {
            var peer = await context.AddIndependentCompleteV1RootAsync();
            File.AppendAllText(peer.StateFilePath, "corrupt-peer");
            return [peer.PackageInstallRoot];
        });
        var before = CaptureInspectionEvidence(fixture.Context, fixture.AdditionalRoots);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            fixture.Service.PlanAndRunAsync("root-0", 1, 0, fixture.UnexpectedCandidate));

        Assert.Equal(0, fixture.Inventory.ReadCalls);
        Assert.Equal(0, fixture.Planner.PlanCalls);
        Assert.Equal(before, CaptureInspectionEvidence(fixture.Context, fixture.AdditionalRoots));
        AssertExecutionLocksReleased(fixture.Context, fixture.AdditionalRoots);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecutionPlanning_PendingPeerRefusesBeforeInventoryAndCandidate()
    {
        using var fixture = await ExecutionFixture.CreateAsync(async context =>
        {
            var next = context.PrepareNextGraphPublication();
            await Assert.ThrowsAsync<SimulatedCrashException>(() => context.Registry().PublishNativeGroupAsync(
                next.Descriptor, context.Requests, next.NextState, CancellationToken.None,
                CrashOnce(RootMembershipRegistry.NativeGroupPublicationPoint.IntentPublished, context.RootBIdentity)));
            return [];
        });
        var before = CaptureInspectionEvidence(fixture.Context);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            fixture.Service.PlanAndRunAsync("root-0", 1, 0, fixture.UnexpectedCandidate));

        Assert.Equal(0, fixture.Inventory.ReadCalls);
        Assert.Equal(0, fixture.Planner.PlanCalls);
        Assert.Equal(before, CaptureInspectionEvidence(fixture.Context));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecutionPlanning_IncompleteInventoryRefusesCandidateAndPreservesStore()
    {
        using var fixture = await ExecutionFixture.CreateAsync(transform: snapshot => new PackageStoreInventorySnapshot(
            snapshot.Root, snapshot.Epoch, false, snapshot.Entries,
            [new PackageStoreInventoryIssue("", "test-incomplete")]));
        var before = CaptureInspectionEvidence(fixture.Context);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            fixture.Service.PlanAndRunAsync("root-0", 1, 0, fixture.UnexpectedCandidate));

        Assert.Equal(1, fixture.Inventory.ReadCalls);
        Assert.Equal(1, fixture.Planner.PlanCalls);
        Assert.Equal(before, CaptureInspectionEvidence(fixture.Context));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecutionPlanning_CandidateFailurePreservesExceptionAndReleasesUnion()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var failure = new InvalidOperationException("candidate-failure");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.PlanAndRunAsync<bool>("root-0", 1, 0, (scope, _) =>
            {
                fixture.Context.AssertAllRootAndMemberLocksHeld();
                scope.Revalidate(CancellationToken.None);
                throw failure;
            }));

        Assert.Same(failure, actual);
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecutionPlanning_CancelledCandidateReleasesUnion()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
            {
                fixture.Context.AssertAllRootAndMemberLocksHeld();
                cancellation.Cancel();
                scope.Revalidate(token);
                return Task.FromResult(true);
            }, cancellation.Token));

        AssertExecutionLocksReleased(fixture.Context);
    }

    private static void AssertExecutionLocksReleased(Context context, params string[] additionalRoots)
    {
        foreach (var path in new[] { context.RootAPath, context.RootBPath }.Concat(additionalRoots))
        {
            using var root = PhysicalStoreTestDirectory.Open(context.Files, path);
            using var control = context.Files.OpenDirectoryChildNoFollow(root, RootMembershipRegistry.ControlDirectoryName);
            foreach (var name in Directory.EnumerateFiles(Path.Combine(path, RootMembershipRegistry.ControlDirectoryName), "*.lock")
                         .Select(Path.GetFileName))
            {
                using var file = context.Files.OpenFileChildNoFollow(control, name!, FileAccess.ReadWrite);
                var acquired = context.Files.TryAcquireExclusiveLock(file).AsTask().GetAwaiter().GetResult();
                Assert.NotNull(acquired);
                acquired.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    private sealed class ExecutionFixture : IDisposable
    {
        private ExecutionFixture(Context context, string[] additionalRoots,
            Func<PackageStoreInventorySnapshot, PackageStoreInventorySnapshot>? transform,
            Action<PackageStoreDeletionJournalPublicationPoint, PackageStoreDeletionJournal>? journalPublicationCheckpoint,
            Action<PackageStoreQuarantineTransitionPoint, PackageStoreDeletionJournal>? quarantineTransitionCheckpoint,
            Action<PackageStoreDeletionExecutionPoint, PackageStoreDeletionJournal>? deletionExecutionCheckpoint)
        {
            Context = context;
            AdditionalRoots = additionalRoots;
            Serializer = new CountingPayloadSerializer(AssertAllLocksHeld);
            var registry = context.Registry(Serializer);
            var catalog = Catalog([context.RootAPath, context.RootBPath, .. additionalRoots]);
            var resolver = new PackageStoreRootResolver(catalog, Options.Create(new PackageStoreMaintenanceOptions()));
            Inventory = new ExecutionInventory(context.Files, AssertAllLocksHeld, transform);
            Planner = new ExecutionPlanner(AssertAllLocksHeld);
            Inspection = new PackageStoreInspectionService(context.Files, registry, catalog, resolver, Inventory, Planner);
            Service = new PackageStoreExecutionService(registry, Inspection, journalPublicationCheckpoint,
                quarantineTransitionCheckpoint, deletionExecutionCheckpoint);
        }

        internal Context Context { get; }
        internal string[] AdditionalRoots { get; }
        internal CountingPayloadSerializer Serializer { get; }
        internal ExecutionInventory Inventory { get; }
        internal ExecutionPlanner Planner { get; }
        internal PackageStoreInspectionService Inspection { get; }
        internal PackageStoreExecutionService Service { get; }

        internal static async Task<ExecutionFixture> CreateAsync(Func<Context, Task<string[]>>? configure = null,
            Func<PackageStoreInventorySnapshot, PackageStoreInventorySnapshot>? transform = null,
            bool withCrossRootGraph = true,
            Action<PackageStoreDeletionJournalPublicationPoint, PackageStoreDeletionJournal>? journalPublicationCheckpoint = null,
            Action<PackageStoreQuarantineTransitionPoint, PackageStoreDeletionJournal>? quarantineTransitionCheckpoint = null,
            Action<PackageStoreDeletionExecutionPoint, PackageStoreDeletionJournal>? deletionExecutionCheckpoint = null,
            IPhysicalStoreFileSystem? fileSystem = null)
        {
            var context = withCrossRootGraph
                ? await Context.CreateWithGraphAsync(fileSystem)
                : await Context.CreateAsync(fileSystem: fileSystem);
            try
            {
                await context.Registry().PublishNativeGroupAsync(context.Descriptor, context.Requests,
                    context.NextState, CancellationToken.None);
                var additional = configure is null ? [] : await configure(context);
                return new ExecutionFixture(context, additional, transform, journalPublicationCheckpoint,
                    quarantineTransitionCheckpoint, deletionExecutionCheckpoint);
            }
            catch
            {
                context.Dispose();
                throw;
            }
        }

        internal Task<bool> UnexpectedCandidate(PackageStoreExecutionService.Context _, CancellationToken token)
            => throw new InvalidOperationException("Candidate work must not run after refused planning.");

        internal void AssertAllLocksHeld()
        {
            Context.AssertAllRootAndMemberLocksHeld();
            foreach (var path in AdditionalRoots)
            {
                using var root = PhysicalStoreTestDirectory.Open(Context.Files, path);
                foreach (var name in Directory.EnumerateFiles(Path.Combine(path, RootMembershipRegistry.ControlDirectoryName), "*.lock")
                             .Select(Path.GetFileName))
                    AssertNativeLockHeld(Context.Files, root, name!);
            }
        }

        internal void AssertNoPlanning()
        {
            Assert.Equal(0, Serializer.ReadCalls);
            Assert.Equal(0, Inventory.ReadCalls);
            Assert.Equal(0, Planner.PlanCalls);
        }

        public void Dispose() => Context.Dispose();
    }

    private sealed class ExecutionInventory(IPhysicalStoreFileSystem files, Action checkpoint,
        Func<PackageStoreInventorySnapshot, PackageStoreInventorySnapshot>? transform)
        : IPackageStoreInventory, IPackageStoreCatalogInventory
    {
        internal int ReadCalls { get; private set; }
        public Task<PackageStoreInventorySnapshot> ReadAsync(PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Execution must use the retained catalog union.");

        public async Task<PackageStoreInventorySnapshot> ReadUnderCatalogOwnershipAsync(
            PhysicalStoreDirectoryHandle heldRoot, PhysicalRootIdentity expectedRoot, long epoch,
            CancellationToken cancellationToken)
        {
            ReadCalls++;
            checkpoint();
            var snapshot = await new PackageStoreInventory(files).ReadUnderCatalogOwnershipAsync(
                heldRoot, expectedRoot, epoch, cancellationToken);
            checkpoint();
            return transform is null ? snapshot : transform(snapshot);
        }
    }

    private sealed class ExecutionPlanner(Action checkpoint) : IPackageStoreRetentionPlanner
    {
        internal int PlanCalls { get; private set; }
        public PackageStoreRetentionPlan Plan(PackageStoreRetentionSnapshot snapshot)
        {
            PlanCalls++;
            checkpoint();
            return new PackageStoreRetentionPlanner().Plan(snapshot);
        }
    }
}
