using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.DeletionRecords;
using Nuplane.Store.Coordination.DeletionSerialization;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Maintenance;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

public sealed partial class RootMembershipNativeGroupPublicationTests
{
    [SupportedPhysicalStoreFact]
    public async Task PreparedJournalPublication_RefusesMissingPublisherBeforeCreatingControlArtifacts()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var installPath = CreateInactiveInstall(fixture.Context.RootAPath);

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive");
            using var candidate = scope.BindEligibleCandidate(row, token);
            var plan = scope.Snapshot.RetentionPlan!;
            var operationId = Guid.NewGuid();
            var candidateId = Guid.NewGuid();
            while (candidateId == operationId)
                candidateId = Guid.NewGuid();
            var journal = candidate.CreatePreparedJournal(scope.Snapshot.Root!, plan.EnrollmentEpoch,
                operationId, candidateId, plan.KeepNewestInactiveVersionsPerPackage!.Value, token);
            var store = new PackageStoreDeletionJournalStore(new NoPublicationFileSystem(fixture.Context.Files));

            var refusal = Assert.Throws<PackageStoreAdmissionException>(() => store.PublishPrepared(
                scope.RootHandle, scope.Snapshot.Root!, journal, candidate.Revalidate,
                candidate.RequireCurrentBaseline, token));

            Assert.Equal(PackageStoreAdmissionReason.UnsupportedFilesystem, refusal.Reason);
            return Task.FromResult(true);
        });

        var controlPath = Path.Combine(fixture.Context.RootAPath, RootMembershipRegistry.ControlDirectoryName);
        Assert.Empty(Directory.EnumerateFiles(controlPath, "prune-*.json*"));
        Assert.True(Directory.Exists(installPath));
        Assert.True(File.Exists(Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName)));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task QuarantineTransition_MovesExactPreparedInstallAndPublishesQuarantinedPhase()
    {
        using var fixture = await ExecutionFixture.CreateAsync(quarantineTransitionCheckpoint: (point, _) =>
        {
            if (point == PackageStoreQuarantineTransitionPoint.AfterDirectoryMove)
                throw new IOException("controlled after-move observation failure");
        });
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath);
        const string payload = "preserve exact package bytes";
        File.WriteAllText(Path.Combine(originalPath, "payload.txt"), payload);
        PackageStoreDeletionJournal? completed = null;

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive");
            var candidate = scope.BindEligibleCandidate(row, token);
            var prepared = scope.PublishPreparedJournal(candidate, token).Journal;
            var quarantined = scope.QuarantinePreparedCandidate(candidate, token);
            completed = quarantined.Journal;

            Assert.Equal(PackageStoreDeletionPhase.Quarantined, quarantined.Journal.Phase);
            Assert.Equal(prepared.CandidateId, quarantined.Journal.CandidateId);
            Assert.Equal(prepared.OperationId, quarantined.Journal.OperationId);
            Assert.Equal(prepared.Manifest.InstallDirectoryIdentity, quarantined.Journal.Manifest.InstallDirectoryIdentity);
            using var root = PhysicalStoreTestDirectory.Open(fixture.Context.Files, fixture.Context.RootAPath);
            using var feed = fixture.Context.Files.OpenDirectoryChildNoFollow(root, "feed");
            using var package = fixture.Context.Files.OpenDirectoryChildNoFollow(feed, "inactive");
            Assert.Null(fixture.Context.Files.InspectChildNoFollow(package, "1.0.0"));
            var moved = Assert.IsType<PhysicalStoreEntryInfo>(fixture.Context.Files.InspectChildNoFollow(
                package, prepared.QuarantineBasename));
            Assert.Equal(PhysicalStoreEntryKind.Directory, moved.Kind);
            Assert.Equal(row.Install.DirectoryIdentity, moved.Identity);
            using var movedDirectory = fixture.Context.Files.OpenDirectoryChildNoFollow(package, prepared.QuarantineBasename);
            var marker = Assert.IsType<PhysicalStoreEntryInfo>(fixture.Context.Files.InspectChildNoFollow(
                movedDirectory, PackageStoreDeletionJournal.CompletionMarkerName));
            Assert.Equal(prepared.CompletionMarkerIdentity, marker.Identity);

            return Task.FromResult(true);
        });

        Assert.NotNull(completed);
        var quarantinePath = Path.Combine(Path.GetDirectoryName(originalPath)!, completed!.QuarantineBasename);
        Assert.False(Directory.Exists(originalPath));
        Assert.True(Directory.Exists(quarantinePath));
        Assert.Equal(payload, File.ReadAllText(Path.Combine(quarantinePath, "payload.txt")));
        Assert.True(File.Exists(Path.Combine(quarantinePath, PackageInstallStore.CompletionMarkerFileName)));

        var controlPath = Path.Combine(fixture.Context.RootAPath, RootMembershipRegistry.ControlDirectoryName);
        var persisted = new PackageStoreDeletionJournalSerializer().Deserialize(
            File.ReadAllBytes(Path.Combine(controlPath, $"prune-{completed.CandidateId:N}.json")));
        Assert.Equal(PackageStoreDeletionPhase.Quarantined, persisted.Phase);
        Assert.Empty(Directory.EnumerateFiles(controlPath, "prune-*.json.stage-*"));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task QuarantineTransition_PreMoveFailureLeavesOriginalAndPreparedJournalUntouched()
    {
        const string error = "controlled before-move failure";
        using var fixture = await ExecutionFixture.CreateAsync(quarantineTransitionCheckpoint: (point, _) =>
        {
            if (point == PackageStoreQuarantineTransitionPoint.BeforeDirectoryMove)
                throw new IOException(error);
        });
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath);
        File.WriteAllText(Path.Combine(originalPath, "payload.txt"), "original");
        PackageStoreDeletionJournal? prepared = null;

        var failure = await Assert.ThrowsAsync<IOException>(() => fixture.Service.PlanAndRunAsync("root-0", 1, 0,
            (scope, token) =>
            {
                var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                    entry => entry.Install.PackageId == "inactive");
                var candidate = scope.BindEligibleCandidate(row, token);
                prepared = scope.PublishPreparedJournal(candidate, token).Journal;
                scope.QuarantinePreparedCandidate(candidate, token);
                return Task.FromResult(true);
            }));

        Assert.Equal(error, failure.Message);
        Assert.True(Directory.Exists(originalPath));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(originalPath)!, prepared!.QuarantineBasename)));
        var controlPath = Path.Combine(fixture.Context.RootAPath, RootMembershipRegistry.ControlDirectoryName);
        var persisted = new PackageStoreDeletionJournalSerializer().Deserialize(
            File.ReadAllBytes(Path.Combine(controlPath, $"prune-{prepared.CandidateId:N}.json")));
        Assert.Equal(PackageStoreDeletionPhase.Prepared, persisted.Phase);
        Assert.Empty(Directory.EnumerateFiles(controlPath, "prune-*.json.stage-*"));
        Assert.Equal("original", File.ReadAllText(Path.Combine(originalPath, "payload.txt")));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task QuarantineTransition_PrePhasePublishFailurePreservesPreparedAndExactStage()
    {
        const string error = "controlled before phase publication";
        Context? nativeContext = null;
        PhysicalFileIdentity? canonicalIdentityAtFailure = null;
        byte[]? canonicalBytesAtFailure = null;
        PhysicalFileIdentity? stageIdentityAtFailure = null;
        byte[]? stageBytesAtFailure = null;
        using var fixture = await ExecutionFixture.CreateAsync(
            configure: context =>
            {
                nativeContext = context;
                return Task.FromResult(Array.Empty<string>());
            },
            journalPublicationCheckpoint: (point, journal) =>
            {
                if (point != PackageStoreDeletionJournalPublicationPoint.BeforeQuarantinedPhasePublish)
                    return;

                var controlPath = Path.Combine(nativeContext!.RootAPath, RootMembershipRegistry.ControlDirectoryName);
                var canonicalName = $"prune-{journal.CandidateId:N}.json";
                var stagePath = Assert.Single(Directory.EnumerateFiles(controlPath, $"{canonicalName}.stage-*"));
                canonicalBytesAtFailure = File.ReadAllBytes(Path.Combine(controlPath, canonicalName));
                stageBytesAtFailure = File.ReadAllBytes(stagePath);
                var persistedPrepared = new PackageStoreDeletionJournalSerializer().Deserialize(canonicalBytesAtFailure);
                var stagedQuarantined = new PackageStoreDeletionJournalSerializer().Deserialize(stageBytesAtFailure);
                Assert.Equal(PackageStoreDeletionPhase.Prepared, persistedPrepared.Phase);
                Assert.Equal(PackageStoreDeletionPhase.Quarantined, stagedQuarantined.Phase);

                using var root = PhysicalStoreTestDirectory.Open(nativeContext.Files, nativeContext.RootAPath);
                using var control = nativeContext.Files.OpenDirectoryChildNoFollow(root,
                    RootMembershipRegistry.ControlDirectoryName);
                canonicalIdentityAtFailure = nativeContext.Files.InspectChildNoFollow(control, canonicalName)!.Identity;
                stageIdentityAtFailure = nativeContext.Files.InspectChildNoFollow(control,
                    Path.GetFileName(stagePath))!.Identity;
                throw new IOException(error);
            });
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath);
        PackageStoreDeletionJournal? prepared = null;

        var failure = await Assert.ThrowsAsync<IOException>(() => fixture.Service.PlanAndRunAsync("root-0", 1, 0,
            (scope, token) =>
            {
                var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                    entry => entry.Install.PackageId == "inactive");
                var candidate = scope.BindEligibleCandidate(row, token);
                prepared = scope.PublishPreparedJournal(candidate, token).Journal;
                scope.QuarantinePreparedCandidate(candidate, token);
                return Task.FromResult(true);
            }));

        Assert.Equal(error, failure.Message);
        var quarantinePath = Path.Combine(Path.GetDirectoryName(originalPath)!, prepared!.QuarantineBasename);
        Assert.False(Directory.Exists(originalPath));
        Assert.True(Directory.Exists(quarantinePath));
        var controlDirectory = Path.Combine(fixture.Context.RootAPath, RootMembershipRegistry.ControlDirectoryName);
        var canonicalPath = Path.Combine(controlDirectory, $"prune-{prepared.CandidateId:N}.json");
        var stageAfterFailure = Assert.Single(Directory.EnumerateFiles(controlDirectory,
            $"{Path.GetFileName(canonicalPath)}.stage-*"));
        Assert.Equal(canonicalBytesAtFailure, File.ReadAllBytes(canonicalPath));
        Assert.Equal(stageBytesAtFailure, File.ReadAllBytes(stageAfterFailure));
        using (var root = PhysicalStoreTestDirectory.Open(fixture.Context.Files, fixture.Context.RootAPath))
        using (var control = fixture.Context.Files.OpenDirectoryChildNoFollow(root,
                   RootMembershipRegistry.ControlDirectoryName))
        {
            Assert.Equal(canonicalIdentityAtFailure,
                fixture.Context.Files.InspectChildNoFollow(control, Path.GetFileName(canonicalPath))!.Identity);
            Assert.Equal(stageIdentityAtFailure,
                fixture.Context.Files.InspectChildNoFollow(control, Path.GetFileName(stageAfterFailure))!.Identity);
        }
        Assert.Equal(PackageStoreDeletionPhase.Prepared,
            new PackageStoreDeletionJournalSerializer().Deserialize(File.ReadAllBytes(canonicalPath)).Phase);
        Assert.Equal(PackageStoreDeletionPhase.Quarantined,
            new PackageStoreDeletionJournalSerializer().Deserialize(File.ReadAllBytes(stageAfterFailure)).Phase);
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task QuarantineTransition_PostPhasePublishExceptionAcceptsExactQuarantinedReadback()
    {
        const string error = "controlled after phase publication";
        Context? nativeContext = null;
        PhysicalFileIdentity? stagedIdentity = null;
        PhysicalFileIdentity? canonicalIdentityAfterPublish = null;
        byte[]? canonicalBytesAfterPublish = null;
        var injected = 0;
        using var fixture = await ExecutionFixture.CreateAsync(
            configure: context =>
            {
                nativeContext = context;
                return Task.FromResult(Array.Empty<string>());
            },
            journalPublicationCheckpoint: (point, journal) =>
            {
                var controlPath = Path.Combine(nativeContext!.RootAPath, RootMembershipRegistry.ControlDirectoryName);
                var canonicalName = $"prune-{journal.CandidateId:N}.json";
                if (point == PackageStoreDeletionJournalPublicationPoint.StageFlushed &&
                    journal.Phase == PackageStoreDeletionPhase.Quarantined)
                {
                    var stagePath = Assert.Single(Directory.EnumerateFiles(controlPath,
                        $"{canonicalName}.stage-*"));
                    using var root = PhysicalStoreTestDirectory.Open(nativeContext.Files, nativeContext.RootAPath);
                    using var control = nativeContext.Files.OpenDirectoryChildNoFollow(root,
                        RootMembershipRegistry.ControlDirectoryName);
                    stagedIdentity = nativeContext.Files.InspectChildNoFollow(control,
                        Path.GetFileName(stagePath))!.Identity;
                    return;
                }

                if (point != PackageStoreDeletionJournalPublicationPoint.AfterQuarantinedPhasePublish)
                    return;

                using (var root = PhysicalStoreTestDirectory.Open(nativeContext.Files, nativeContext.RootAPath))
                using (var control = nativeContext.Files.OpenDirectoryChildNoFollow(root,
                           RootMembershipRegistry.ControlDirectoryName))
                {
                    canonicalIdentityAfterPublish = nativeContext.Files.InspectChildNoFollow(control,
                        canonicalName)!.Identity;
                }
                canonicalBytesAfterPublish = File.ReadAllBytes(Path.Combine(controlPath, canonicalName));
                Assert.Empty(Directory.EnumerateFiles(controlPath, $"{canonicalName}.stage-*"));
                injected++;
                throw new IOException(error);
            });
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath);
        File.WriteAllText(Path.Combine(originalPath, "payload.txt"), "still quarantined");
        PackageStoreDeletionJournalStore.PublishedJournal? result = null;

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive");
            var candidate = scope.BindEligibleCandidate(row, token);
            scope.PublishPreparedJournal(candidate, token);
            result = scope.QuarantinePreparedCandidate(candidate, token);
            return Task.FromResult(true);
        });

        Assert.Equal(1, injected);
        Assert.NotNull(result);
        Assert.Equal(PackageStoreDeletionPhase.Quarantined, result!.Journal.Phase);
        var quarantinePath = Path.Combine(Path.GetDirectoryName(originalPath)!, result.Journal.QuarantineBasename);
        Assert.False(Directory.Exists(originalPath));
        Assert.True(Directory.Exists(quarantinePath));
        Assert.Equal("still quarantined", File.ReadAllText(Path.Combine(quarantinePath, "payload.txt")));
        var controlDirectory = Path.Combine(fixture.Context.RootAPath, RootMembershipRegistry.ControlDirectoryName);
        var canonicalPath = Path.Combine(controlDirectory, $"prune-{result.Journal.CandidateId:N}.json");
        Assert.Equal(canonicalBytesAfterPublish, File.ReadAllBytes(canonicalPath));
        Assert.Equal(stagedIdentity, canonicalIdentityAfterPublish);
        Assert.Empty(Directory.EnumerateFiles(controlDirectory, $"{Path.GetFileName(canonicalPath)}.stage-*"));
        Assert.Equal(PackageStoreDeletionPhase.Quarantined,
            new PackageStoreDeletionJournalSerializer().Deserialize(File.ReadAllBytes(canonicalPath)).Phase);
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task QuarantineTransition_RefusesInstallTreeChangedAfterPreparedPublication()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath);
        PackageStoreDeletionJournal? prepared = null;

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive");
            var candidate = scope.BindEligibleCandidate(row, token);
            prepared = scope.PublishPreparedJournal(candidate, token).Journal;
            File.WriteAllText(Path.Combine(originalPath, "unexpected.txt"), "changed after Prepared");
            var refusal = Assert.Throws<PackageStoreAdmissionException>(() =>
                scope.QuarantinePreparedCandidate(candidate, token));
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
            return Task.FromResult(true);
        });

        Assert.True(Directory.Exists(originalPath));
        Assert.Equal("changed after Prepared", File.ReadAllText(Path.Combine(originalPath, "unexpected.txt")));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(originalPath)!, prepared!.QuarantineBasename)));
        var controlPath = Path.Combine(fixture.Context.RootAPath, RootMembershipRegistry.ControlDirectoryName);
        var persisted = new PackageStoreDeletionJournalSerializer().Deserialize(
            File.ReadAllBytes(Path.Combine(controlPath, $"prune-{prepared.CandidateId:N}.json")));
        Assert.Equal(PackageStoreDeletionPhase.Prepared, persisted.Phase);
        Assert.Empty(Directory.EnumerateFiles(controlPath, "prune-*.json.stage-*"));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task QuarantineInventoryJoin_RefusesOrphanReservedPrefixWithoutMovingIt()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath);
        File.WriteAllText(Path.Combine(originalPath, "payload.txt"), "unclaimed");
        var quarantineName = PackageStoreDeletionJournal.QuarantineBasenameFor(Guid.NewGuid());
        var quarantinePath = Path.Combine(Path.GetDirectoryName(originalPath)!, quarantineName);
        Directory.Move(originalPath, quarantinePath);
        var controlPath = Path.Combine(fixture.Context.RootAPath, RootMembershipRegistry.ControlDirectoryName);

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            fixture.Service.PlanAndRunAsync("root-0", 1, 0, fixture.UnexpectedCandidate));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.False(Directory.Exists(originalPath));
        Assert.True(Directory.Exists(quarantinePath));
        Assert.Equal("unclaimed", File.ReadAllText(Path.Combine(quarantinePath, "payload.txt")));
        Assert.Empty(Directory.EnumerateFiles(controlPath, "prune-*.json*"));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task QuarantineInventoryJoin_OverlaysLogicalQuarantineIntoFreshRetentionRanking()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var lowerPath = CreateInactiveInstall(fixture.Context.RootAPath, "inactive", "1.0.0");
        var higherPath = CreateInactiveInstall(fixture.Context.RootAPath, "inactive", "2.0.0");
        File.WriteAllText(Path.Combine(higherPath, "payload.txt"), "higher version remains quarantined");
        PackageStoreDeletionJournal? quarantined = null;

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive" && entry.Install.Version == "2.0.0");
            var candidate = scope.BindEligibleCandidate(row, token);
            scope.PublishPreparedJournal(candidate, token);
            quarantined = scope.QuarantinePreparedCandidate(candidate, token).Journal;
            return Task.FromResult(true);
        });

        await fixture.Service.PlanAndRunAsync("root-0", 1, 1, (scope, _) =>
        {
            var high = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive" && entry.Install.Version == "2.0.0");
            var low = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive" && entry.Install.Version == "1.0.0");
            Assert.Equal(PackageStoreRetentionClassification.Retained, high.Classification);
            Assert.Contains(PackageStoreRetentionReason.WithinInactiveRetentionBudget, high.Reasons);
            Assert.Equal(PackageStoreRetentionClassification.Eligible, low.Classification);
            Assert.Contains(PackageStoreRetentionReason.OutsideInactiveRetentionBudget, low.Reasons);
            return Task.FromResult(true);
        });

        Assert.NotNull(quarantined);
        Assert.True(Directory.Exists(lowerPath));
        Assert.False(Directory.Exists(higherPath));
        Assert.True(Directory.Exists(Path.Combine(Path.GetDirectoryName(higherPath)!, quarantined!.QuarantineBasename)));
        Assert.Equal("higher version remains quarantined", File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(higherPath)!, quarantined.QuarantineBasename, "payload.txt")));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task QuarantineInventoryJoin_AdvancesMovedPreparedRecordOnlyAfterFreshRebind()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath, "inactive", "1.0.0");
        CreateInactiveInstall(fixture.Context.RootAPath, "inactive", "2.0.0");
        File.WriteAllText(Path.Combine(originalPath, "payload.txt"), "moved before the prior process ended");
        PackageStoreDeletionJournal? prepared = null;

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive" && entry.Install.Version == "1.0.0");
            var candidate = scope.BindEligibleCandidate(row, token);
            prepared = scope.PublishPreparedJournal(candidate, token).Journal;
            var parent = candidate.PrepareForMove(token);
            var publication = Assert.IsAssignableFrom<IPhysicalStoreDirectoryPublicationFileSystem>(fixture.Context.Files);
            var source = PhysicalStoreDirectoryPublicationChecks.PreparePublish(fixture.Context.Files, publication,
                parent.PackageParentHandle, parent.OriginalBasename, parent.InstallDirectoryIdentity,
                prepared.QuarantineBasename);
            Assert.Equal(parent.PackageParentIdentity, source.ParentIdentity);
            var moved = publication.PublishDirectoryNoReplaceAt(parent.PackageParentHandle,
                parent.OriginalBasename, parent.InstallDirectoryIdentity, prepared.QuarantineBasename);
            Assert.Equal(parent.InstallDirectoryIdentity, moved.Identity);
            return Task.FromResult(true);
        });

        Assert.False(Directory.Exists(originalPath));
        Assert.True(Directory.Exists(Path.Combine(Path.GetDirectoryName(originalPath)!, prepared!.QuarantineBasename)));
        Assert.Equal(PackageStoreDeletionPhase.Prepared, prepared.Phase);

        PackageStoreDeletionJournal? advanced = null;
        await fixture.Service.PlanAndRunAsync("root-0", 1, 1, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install == prepared.OriginalInstall);
            Assert.Equal(PackageStoreRetentionClassification.Eligible, row.Classification);
            var higher = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive" && entry.Install.Version == "2.0.0");
            Assert.Equal(PackageStoreRetentionClassification.Retained, higher.Classification);
            var quarantine = scope.BindEligibleQuarantinedCandidate(row, token);
            advanced = scope.AdvancePreparedQuarantine(quarantine, token).Journal;
            return Task.FromResult(true);
        });

        Assert.Equal(PackageStoreDeletionPhase.Quarantined, advanced!.Phase);
        Assert.Equal(prepared.CandidateId, advanced.CandidateId);
        Assert.Equal(0, advanced.KeepNewestInactiveVersionsPerPackage);
        var quarantinePath = Path.Combine(Path.GetDirectoryName(originalPath)!, advanced.QuarantineBasename);
        Assert.True(Directory.Exists(quarantinePath));
        Assert.Equal("moved before the prior process ended", File.ReadAllText(Path.Combine(quarantinePath, "payload.txt")));
        var controlPath = Path.Combine(fixture.Context.RootAPath, RootMembershipRegistry.ControlDirectoryName);
        var persisted = new PackageStoreDeletionJournalSerializer().Deserialize(
            File.ReadAllBytes(Path.Combine(controlPath, $"prune-{advanced.CandidateId:N}.json")));
        Assert.Equal(PackageStoreDeletionPhase.Quarantined, persisted.Phase);
        Assert.Empty(Directory.EnumerateFiles(controlPath, "prune-*.json.stage-*"));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task QuarantineInventoryJoin_LeavesOriginalSlotReplacementAsIdentityConflict()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath);
        File.WriteAllText(Path.Combine(originalPath, "payload.txt"), "journal-bound original");
        PackageStoreDeletionJournal? quarantined = null;
        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive");
            var candidate = scope.BindEligibleCandidate(row, token);
            scope.PublishPreparedJournal(candidate, token);
            quarantined = scope.QuarantinePreparedCandidate(candidate, token).Journal;
            return Task.FromResult(true);
        });

        var replacementPath = CreateInactiveInstall(fixture.Context.RootAPath);
        File.WriteAllText(Path.Combine(replacementPath, "payload.txt"), "replacement remains untouched");
        var candidateWorkCalls = 0;
        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            fixture.Service.PlanAndRunAsync("root-0", 1, 0, (_, _) =>
            {
                candidateWorkCalls++;
                return Task.FromResult(true);
            }));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.Equal(0, candidateWorkCalls);

        // Inspect the exact logical overlay and its planner consequence separately. The execution boundary
        // correctly refuses this unresolved plan before candidate work can observe its rows.
        using (var root = PhysicalStoreTestDirectory.Open(fixture.Context.Files, fixture.Context.RootAPath))
        using (var join = new PackageStoreDeletionExecutionJoin(
                   new PackageStoreDeletionJournalStore(fixture.Context.Files),
                   new PackageStoreDeletionCandidateBinder(fixture.Context.Files), root,
                   fixture.Context.RootAIdentity, 1, _ => { }, CancellationToken.None))
        {
            var inventory = await new PackageStoreInventory(fixture.Context.Files)
                .ReadUnderCatalogOwnershipAsync(root, fixture.Context.RootAIdentity, 1, CancellationToken.None);
            var completed = inventory.Entries
                .Where(static entry => entry.Kind == PackageStoreInventoryEntryKind.CompletedInstallCandidate)
                .Select(static entry => entry.InstallIdentity!)
                .ToList();
            var overlay = join.BuildOverlay(inventory, CancellationToken.None);
            completed.AddRange(overlay);
            var plan = new PackageStoreRetentionPlanner().Plan(new PackageStoreRetentionSnapshot(
                fixture.Context.RootAIdentity, 1, PackageStoreRetentionInventoryStatus.Complete,
                completed, PackageStoreRetentionProtectionKnowledge.Known, [], 0));
            var conflictingRows = plan.Entries.Where(entry => entry.Install.RootRelativeInstallPath ==
                quarantined!.OriginalInstall.RootRelativeInstallPath).ToArray();
            Assert.Equal(2, conflictingRows.Length);
            Assert.All(conflictingRows, row =>
            {
                Assert.Equal(PackageStoreRetentionClassification.Refused, row.Classification);
                Assert.Contains(PackageStoreRetentionReason.IdentityConflict, row.Reasons);
            });

            var withoutOverlayPlan = new PackageStoreRetentionPlanner().Plan(new PackageStoreRetentionSnapshot(
                fixture.Context.RootAIdentity, 1, PackageStoreRetentionInventoryStatus.Complete,
                inventory.Entries
                    .Where(static entry => entry.Kind == PackageStoreInventoryEntryKind.CompletedInstallCandidate)
                    .Select(static entry => entry.InstallIdentity!).ToArray(),
                PackageStoreRetentionProtectionKnowledge.Known, [], 0));
            var replacementWithoutOverlay = Assert.Single(withoutOverlayPlan.Entries,
                entry => entry.Install.RootRelativeInstallPath == quarantined!.OriginalInstall.RootRelativeInstallPath);
            Assert.Equal(PackageStoreRetentionClassification.Eligible, replacementWithoutOverlay.Classification);
        }

        var quarantinePath = Path.Combine(Path.GetDirectoryName(originalPath)!, quarantined!.QuarantineBasename);
        Assert.True(Directory.Exists(quarantinePath));
        Assert.True(Directory.Exists(replacementPath));
        Assert.Equal("journal-bound original", File.ReadAllText(Path.Combine(quarantinePath, "payload.txt")));
        Assert.Equal("replacement remains untouched", File.ReadAllText(Path.Combine(replacementPath, "payload.txt")));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task QuarantineBinding_FinalRevalidationFailureClosesEveryMintedHandle()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath);
        PackageStoreDeletionJournal? prepared = null;

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive");
            var candidate = scope.BindEligibleCandidate(row, token);
            prepared = scope.PublishPreparedJournal(candidate, token).Journal;
            var parent = candidate.PrepareForMove(token);
            var publication = Assert.IsAssignableFrom<IPhysicalStoreDirectoryPublicationFileSystem>(fixture.Context.Files);
            publication.PublishDirectoryNoReplaceAt(parent.PackageParentHandle,
                parent.OriginalBasename, parent.InstallDirectoryIdentity, prepared.QuarantineBasename);
            return Task.FromResult(true);
        });

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var discovered = Assert.Single(new PackageStoreDeletionJournalStore(fixture.Context.Files).Discover(
                scope.RootHandle, scope.Snapshot.Root!, scope.Snapshot.EnrollmentEpoch!.Value, token));
            var tracking = new TrackingCandidateFileSystem(fixture.Context.Files);
            var binder = new PackageStoreDeletionCandidateBinder(tracking);
            var validationCalls = 0;

            var refusal = Assert.Throws<PackageStoreAdmissionException>(() => binder.BindQuarantinedCandidate(
                discovered, scope.RootHandle, _ =>
                {
                    if (++validationCalls == 2)
                        throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                            "controlled final revalidation failure", scope.Snapshot.Root);
                }, token));

            Assert.Equal("controlled final revalidation failure", refusal.Message);
            Assert.Equal(2, validationCalls);
            Assert.True(tracking.OpenedHandles.Count >= 4,
                "The binding should have opened the package ancestry, quarantine directory, and marker before final revalidation.");
            foreach (var opened in tracking.OpenedHandles)
            {
                var expired = Assert.Throws<PackageStoreAdmissionException>(() => tracking.InspectHandle(opened));
                Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, expired.Reason);
            }
            return Task.FromResult(true);
        });

        var quarantinePath = Path.Combine(Path.GetDirectoryName(originalPath)!, prepared!.QuarantineBasename);
        Assert.False(Directory.Exists(originalPath));
        Assert.True(Directory.Exists(quarantinePath));
        Assert.True(File.Exists(Path.Combine(quarantinePath, PackageInstallStore.CompletionMarkerFileName)));
        AssertExecutionLocksReleased(fixture.Context);
    }

    private sealed class NoPublicationFileSystem(IPhysicalStoreFileSystem inner)
        : DelegatingDeletionNativeFileSystem(inner);

    private sealed class TrackingCandidateFileSystem(IPhysicalStoreFileSystem inner)
        : DelegatingDeletionNativeFileSystem(inner)
    {
        private readonly List<PhysicalStoreHandle> _openedHandles = [];

        internal IReadOnlyList<PhysicalStoreHandle> OpenedHandles => _openedHandles;

        public override PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(
            PhysicalStoreDirectoryHandle parent, string singleName)
        {
            var handle = Inner.OpenDirectoryChildNoFollow(parent, singleName);
            _openedHandles.Add(handle);
            return handle;
        }
        public override PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory)
        {
            var handle = Inner.OpenParentDirectory(directory);
            _openedHandles.Add(handle);
            return handle;
        }
        public override PhysicalStoreFileHandle OpenFileChildNoFollow(
            PhysicalStoreDirectoryHandle parent, string singleName, FileAccess access)
        {
            var handle = Inner.OpenFileChildNoFollow(parent, singleName, access);
            _openedHandles.Add(handle);
            return handle;
        }
    }

    private abstract class DelegatingDeletionNativeFileSystem(IPhysicalStoreFileSystem inner)
        : IPhysicalStoreFileSystem, IPhysicalStoreNameFileSystem, IPhysicalStoreDirectoryNameFileSystem,
            IPhysicalStoreDirectoryEnumerationFileSystem, IPhysicalStoreDeletionFileSystem
    {
        protected IPhysicalStoreFileSystem Inner { get; } = inner;
        private readonly IPhysicalStoreNameFileSystem _names = (IPhysicalStoreNameFileSystem)inner;
        private readonly IPhysicalStoreDirectoryNameFileSystem _directories =
            (IPhysicalStoreDirectoryNameFileSystem)inner;
        private readonly IPhysicalStoreDirectoryEnumerationFileSystem _enumeration =
            (IPhysicalStoreDirectoryEnumerationFileSystem)inner;


        public virtual PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor) => Inner.OpenNamespaceRoot(anchor);
        public virtual PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => Inner.InspectChildNoFollow(parent, singleName);
        public virtual PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => Inner.OpenDirectoryChildNoFollow(parent, singleName);
        public virtual PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory)
            => Inner.OpenParentDirectory(directory);
        public virtual PhysicalStoreFileHandle OpenFileChildNoFollow(
            PhysicalStoreDirectoryHandle parent, string singleName, FileAccess access)
            => Inner.OpenFileChildNoFollow(parent, singleName, access);
        public virtual string ReadLinkTargetNoFollow(
            PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedLinkIdentity)
            => Inner.ReadLinkTargetNoFollow(parent, singleName, expectedLinkIdentity);
        public virtual PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle) => Inner.InspectHandle(handle);
        public virtual PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(
            PhysicalStoreDirectoryHandle parent, string singleName)
            => Inner.CreateDirectoryExclusiveAt(parent, singleName);
        public virtual PhysicalStoreFileHandle CreateFileExclusiveAt(
            PhysicalStoreDirectoryHandle parent, string singleName)
            => Inner.CreateFileExclusiveAt(parent, singleName);
        public virtual byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes)
            => Inner.ReadControlFile(file, maximumBytes);
        public virtual void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
            => Inner.WriteNewControlFile(file, contents);
        public virtual ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
            => Inner.TryAcquireExclusiveLock(file);
        public PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
            => _names.ObserveDirectoryNameSemantics(parent);
        public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(
            PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedFileIdentity)
            => _names.ObserveCanonicalFileNameNoFollow(parent, singleName, expectedFileIdentity);
        public PhysicalStoreCanonicalName ObserveCanonicalDirectoryNameNoFollow(
            PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedDirectoryIdentity)
            => _directories.ObserveCanonicalDirectoryNameNoFollow(parent, singleName, expectedDirectoryIdentity);
        public IReadOnlyList<string> EnumerateChildNamesNoFollow(PhysicalStoreDirectoryHandle parent, int maximumEntries)
            => _enumeration.EnumerateChildNamesNoFollow(parent, maximumEntries);
        public virtual void RemoveFileAt(PhysicalStoreDirectoryHandle parent, string singleName,
            PhysicalFileIdentity expectedIdentity)
            => ((IPhysicalStoreDeletionFileSystem)Inner).RemoveFileAt(parent, singleName, expectedIdentity);
        public virtual void RemoveEmptyDirectoryAt(PhysicalStoreDirectoryHandle parent, string singleName,
            PhysicalFileIdentity expectedIdentity)
            => ((IPhysicalStoreDeletionFileSystem)Inner).RemoveEmptyDirectoryAt(parent, singleName, expectedIdentity);

    }
}
