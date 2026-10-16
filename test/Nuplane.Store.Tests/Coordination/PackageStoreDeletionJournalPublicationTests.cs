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
    public async Task PreparedJournalPublication_PublishesExactCandidateAndReceiptExpiresWithScope()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var installPath = CreateInactiveInstall(fixture.Context.RootAPath);
        var secondInstallPath = CreateInactiveInstall(fixture.Context.RootAPath, "inactive-second");
        File.WriteAllText(Path.Combine(installPath, "payload.txt"), "keep until a later executor");
        File.WriteAllText(Path.Combine(secondInstallPath, "payload.txt"), "also keep");
        PackageStoreExecutionService.Context.PreparedJournalReceipt? escapedReceipt = null;
        PackageStoreDeletionCandidate? candidate = null;
        PackageStoreDeletionJournal? secondJournal = null;

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive");
            Assert.Equal(PackageStoreRetentionClassification.Eligible, row.Classification);
            candidate = scope.BindEligibleCandidate(row, token);
            var receipt = scope.PublishPreparedJournal(candidate, token);
            escapedReceipt = receipt;

            var journal = receipt.Journal;
            Assert.Equal(PackageStoreDeletionPhase.Prepared, journal.Phase);
            Assert.Equal(scope.Snapshot.Root, journal.StoreRoot);
            Assert.Equal(scope.Snapshot.EnrollmentEpoch, journal.EnrollmentEpoch);
            Assert.Equal(row.Install, journal.OriginalInstall);
            Assert.Equal(row.Install.DirectoryIdentity, journal.Manifest.InstallDirectoryIdentity);
            Assert.Equal(0, journal.Manifest.RequireExactSubset(candidate.Manifest));
            Assert.Equal($"prune-{journal.CandidateId:N}.json", receipt.CanonicalBasename);
            Assert.NotEqual(Guid.Empty, journal.OperationId);
            Assert.NotEqual(Guid.Empty, journal.CandidateId);
            Assert.NotEqual(journal.OperationId, journal.CandidateId);
            using (var root = PhysicalStoreTestDirectory.Open(fixture.Context.Files, fixture.Context.RootAPath))
            using (var control = fixture.Context.Files.OpenDirectoryChildNoFollow(root,
                       RootMembershipRegistry.ControlDirectoryName))
            {
                var canonical = Assert.IsType<PhysicalStoreEntryInfo>(fixture.Context.Files.InspectChildNoFollow(
                    control, receipt.CanonicalBasename));
                Assert.Equal(receipt.JournalFileIdentity, canonical.Identity);
            }

            receipt.Revalidate(token);
            var parent = candidate.PrepareForMove(token);
            parent.Revalidate(token);
            receipt.Revalidate(token);
            Assert.True(Directory.Exists(installPath));
            Assert.Equal("keep until a later executor", File.ReadAllText(Path.Combine(installPath, "payload.txt")));
            var secondRow = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive-second");
            var secondReceipt = scope.PublishPreparedJournal(scope.BindEligibleCandidate(secondRow, token), token);
            secondJournal = secondReceipt.Journal;
            Assert.Equal(journal.OperationId, secondJournal.OperationId);
            Assert.NotEqual(journal.CandidateId, secondJournal.CandidateId);
            secondReceipt.Revalidate(token);
            Assert.Equal("also keep", File.ReadAllText(Path.Combine(secondInstallPath, "payload.txt")));
            candidate.Dispose();
            var disposedCandidate = Assert.Throws<PackageStoreAdmissionException>(() => receipt.Revalidate(token));
            Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, disposedCandidate.Reason);
            return Task.FromResult(true);
        });

        Assert.NotNull(escapedReceipt);
        Assert.NotNull(secondJournal);
        var expired = Assert.Throws<PackageStoreAdmissionException>(() => escapedReceipt!.Revalidate(CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, expired.Reason);
        Assert.True(File.Exists(Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName)));
        Assert.Equal("keep until a later executor", File.ReadAllText(Path.Combine(installPath, "payload.txt")));
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(fixture.Context.RootAPath,
            RootMembershipRegistry.ControlDirectoryName), "prune-*.json").Count());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Context.RootAPath,
            RootMembershipRegistry.ControlDirectoryName), "prune-*.json.stage-*"));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task PreparedJournalPublication_PreNativeFailurePreservesExactStageAndRefusesSameContextRetry()
    {
        using var fixture = await ExecutionFixture.CreateAsync(journalPublicationCheckpoint: (point, _) =>
        {
            if (point == PackageStoreDeletionJournalPublicationPoint.BeforeNativePublish)
                throw new InvalidOperationException("controlled pre-publication failure");
        });
        var installPath = CreateInactiveInstall(fixture.Context.RootAPath);
        var controlPath = Path.Combine(fixture.Context.RootAPath, RootMembershipRegistry.ControlDirectoryName);

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive");
            var candidate = scope.BindEligibleCandidate(row, token);
            var failure = Assert.Throws<InvalidOperationException>(() =>
                scope.PublishPreparedJournal(candidate, token));
            Assert.Equal("controlled pre-publication failure", failure.Message);
            var retry = Assert.Throws<PackageStoreAdmissionException>(() =>
                scope.PublishPreparedJournal(candidate, token));
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, retry.Reason);
            return Task.FromResult(true);
        });

        var stage = Assert.Single(Directory.EnumerateFiles(controlPath, "prune-*.json.stage-*"));
        var stageBytes = File.ReadAllBytes(stage);
        Assert.NotEmpty(stageBytes);
        var stageJournal = new PackageStoreDeletionJournalSerializer().Deserialize(stageBytes);
        Assert.Equal(PackageStoreDeletionPhase.Prepared, stageJournal.Phase);
        Assert.Empty(Directory.EnumerateFiles(controlPath, "prune-*.json"));
        Assert.True(File.Exists(Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName)));

        Assert.Equal(stageBytes, File.ReadAllBytes(stage));
        Assert.Empty(Directory.EnumerateFiles(controlPath, "prune-*.json"));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task PreparedJournalPublication_NativeNoReplaceCollisionPreservesStageAndCanonicalSentinel()
    {
        const string sentinel = "pre-existing canonical bytes";
        Context? nativeContext = null;
        PhysicalFileIdentity? canonicalIdentityAtCollision = null;
        PhysicalFileIdentity? stageIdentityAtCollision = null;
        using var fixture = await ExecutionFixture.CreateAsync(
            context =>
            {
                nativeContext = context;
                return Task.FromResult(Array.Empty<string>());
            },
            journalPublicationCheckpoint: (point, journal) =>
            {
                if (point != PackageStoreDeletionJournalPublicationPoint.BeforeNativePublish)
                    return;

                var controlPath = Path.Combine(nativeContext!.RootAPath, RootMembershipRegistry.ControlDirectoryName);
                var canonicalName = $"prune-{journal.CandidateId:N}.json";
                var stagePath = Assert.Single(Directory.EnumerateFiles(controlPath,
                    $"{canonicalName}.stage-*"));
                File.WriteAllText(Path.Combine(controlPath, canonicalName), sentinel);

                using var root = PhysicalStoreTestDirectory.Open(nativeContext.Files, nativeContext.RootAPath);
                using var control = nativeContext.Files.OpenDirectoryChildNoFollow(root,
                    RootMembershipRegistry.ControlDirectoryName);
                canonicalIdentityAtCollision = nativeContext.Files.InspectChildNoFollow(control, canonicalName)!.Identity;
                stageIdentityAtCollision = nativeContext.Files.InspectChildNoFollow(control,
                    Path.GetFileName(stagePath))!.Identity;
            });
        var installPath = CreateInactiveInstall(fixture.Context.RootAPath);
        var controlDirectory = Path.Combine(fixture.Context.RootAPath, RootMembershipRegistry.ControlDirectoryName);
        PackageStoreExecutionService.Context.PreparedJournalReceipt? receipt = null;

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
            {
                var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                    entry => entry.Install.PackageId == "inactive");
                receipt = scope.PublishPreparedJournal(scope.BindEligibleCandidate(row, token), token);
                return Task.FromResult(true);
            }));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.Null(receipt);
        var canonicalPath = Assert.Single(Directory.EnumerateFiles(controlDirectory, "prune-*.json"));
        Assert.Equal(sentinel, File.ReadAllText(canonicalPath));
        var stage = Assert.Single(Directory.EnumerateFiles(controlDirectory, "prune-*.json.stage-*"));
        var stageBytes = File.ReadAllBytes(stage);
        var stageJournal = new PackageStoreDeletionJournalSerializer().Deserialize(stageBytes);
        Assert.Equal(PackageStoreDeletionPhase.Prepared, stageJournal.Phase);
        using (var root = PhysicalStoreTestDirectory.Open(fixture.Context.Files, fixture.Context.RootAPath))
        using (var control = fixture.Context.Files.OpenDirectoryChildNoFollow(root,
                   RootMembershipRegistry.ControlDirectoryName))
        {
            Assert.Equal(canonicalIdentityAtCollision,
                fixture.Context.Files.InspectChildNoFollow(control, Path.GetFileName(canonicalPath))!.Identity);
            Assert.Equal(stageIdentityAtCollision,
                fixture.Context.Files.InspectChildNoFollow(control, Path.GetFileName(stage))!.Identity);
        }
        Assert.True(File.Exists(Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName)));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task PreparedJournalPublication_AcceptsPostPublishExceptionOnlyAfterExactNativeReadback()
    {
        using var fixture = await ExecutionFixture.CreateAsync(journalPublicationCheckpoint: (point, _) =>
        {
            if (point == PackageStoreDeletionJournalPublicationPoint.AfterNativePublish)
                throw new InvalidOperationException("controlled post-publication failure");
        });
        var installPath = CreateInactiveInstall(fixture.Context.RootAPath);
        PackageStoreExecutionService.Context.PreparedJournalReceipt? receipt = null;
        string? canonicalBasename = null;

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive");
            receipt = scope.PublishPreparedJournal(scope.BindEligibleCandidate(row, token), token);
            receipt.Revalidate(token);
            canonicalBasename = receipt.CanonicalBasename;
            Assert.Equal(PackageStoreDeletionPhase.Prepared, receipt.Journal.Phase);
            return Task.FromResult(true);
        });

        Assert.NotNull(receipt);
        var path = Path.Combine(fixture.Context.RootAPath, RootMembershipRegistry.ControlDirectoryName,
            canonicalBasename!);
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName)));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "prune-*.json.stage-*"));
        var expired = Assert.Throws<PackageStoreAdmissionException>(() => receipt.Revalidate(CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, expired.Reason);
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task PreparedJournalPublication_RefusesTamperedStageWithoutCleanupOrRetry()
    {
        Context? nativeContext = null;
        using var fixture = await ExecutionFixture.CreateAsync(
            context =>
            {
                nativeContext = context;
                return Task.FromResult(Array.Empty<string>());
            },
            journalPublicationCheckpoint: (point, journal) =>
            {
                if (point != PackageStoreDeletionJournalPublicationPoint.StageFlushed)
                    return;
                var control = Path.Combine(nativeContext!.RootAPath, RootMembershipRegistry.ControlDirectoryName);
                var stage = Assert.Single(Directory.EnumerateFiles(control,
                    $"prune-{journal.CandidateId:N}.json.stage-*"));
                File.WriteAllText(stage, "tampered stage bytes");
            });
        var installPath = CreateInactiveInstall(fixture.Context.RootAPath);
        var controlPath = Path.Combine(fixture.Context.RootAPath, RootMembershipRegistry.ControlDirectoryName);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
            {
                var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                    entry => entry.Install.PackageId == "inactive");
                scope.PublishPreparedJournal(scope.BindEligibleCandidate(row, token), token);
                return Task.FromResult(true);
            }));

        var stage = Assert.Single(Directory.EnumerateFiles(controlPath, "prune-*.json.stage-*"));
        Assert.Equal("tampered stage bytes", File.ReadAllText(stage));
        Assert.Empty(Directory.EnumerateFiles(controlPath, "prune-*.json"));
        Assert.True(File.Exists(Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName)));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task PreparedJournalPublication_RefusesReplacedStageIdentityAndPreservesBothNames()
    {
        Context? nativeContext = null;
        PhysicalFileIdentity? originalStageIdentity = null;
        using var fixture = await ExecutionFixture.CreateAsync(
            context =>
            {
                nativeContext = context;
                return Task.FromResult(Array.Empty<string>());
            },
            journalPublicationCheckpoint: (point, journal) =>
            {
                if (point != PackageStoreDeletionJournalPublicationPoint.StageFlushed)
                    return;
                var control = Path.Combine(nativeContext!.RootAPath, RootMembershipRegistry.ControlDirectoryName);
                var stage = Assert.Single(Directory.EnumerateFiles(control,
                    $"prune-{journal.CandidateId:N}.json.stage-*"));
                using (var root = PhysicalStoreTestDirectory.Open(nativeContext!.Files, nativeContext.RootAPath))
                using (var controlHandle = nativeContext.Files.OpenDirectoryChildNoFollow(root,
                           RootMembershipRegistry.ControlDirectoryName))
                {
                    originalStageIdentity = nativeContext.Files.InspectChildNoFollow(
                        controlHandle, Path.GetFileName(stage))!.Identity;
                }
                var saved = stage + ".preserved";
                File.Move(stage, saved);
                File.WriteAllBytes(stage, new PackageStoreDeletionJournalSerializer().Serialize(journal));
            });
        var installPath = CreateInactiveInstall(fixture.Context.RootAPath);
        var controlPath = Path.Combine(fixture.Context.RootAPath, RootMembershipRegistry.ControlDirectoryName);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
            {
                var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                    entry => entry.Install.PackageId == "inactive");
                scope.PublishPreparedJournal(scope.BindEligibleCandidate(row, token), token);
                return Task.FromResult(true);
            }));

        var names = Directory.EnumerateFiles(controlPath, "prune-*").ToArray();
        Assert.Equal(2, names.Length);
        Assert.Contains(names, name => name.EndsWith(".preserved", StringComparison.Ordinal));
        var replacementStage = Assert.Single(names, name => name.Contains(".stage-", StringComparison.Ordinal) &&
                                                            !name.EndsWith(".preserved", StringComparison.Ordinal));
        using (var root = PhysicalStoreTestDirectory.Open(fixture.Context.Files, fixture.Context.RootAPath))
        using (var control = fixture.Context.Files.OpenDirectoryChildNoFollow(root,
                   RootMembershipRegistry.ControlDirectoryName))
        {
            var savedIdentity = fixture.Context.Files.InspectChildNoFollow(control,
                Path.GetFileName(replacementStage) + ".preserved")!.Identity;
            var replacementIdentity = fixture.Context.Files.InspectChildNoFollow(control,
                Path.GetFileName(replacementStage))!.Identity;
            Assert.Equal(originalStageIdentity, savedIdentity);
            Assert.NotEqual(originalStageIdentity, replacementIdentity);
        }
        Assert.Empty(Directory.EnumerateFiles(controlPath, "prune-*.json"));
        Assert.True(File.Exists(Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName)));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task PreparedJournalPublication_RefusesUnknownReservedAliasAndPreservesIt()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var installPath = CreateInactiveInstall(fixture.Context.RootAPath);
        var controlPath = Path.Combine(fixture.Context.RootAPath, RootMembershipRegistry.ControlDirectoryName);
        var aliasPath = Path.Combine(controlPath, "Prune-not-a-canonical-name.json");
        File.WriteAllText(aliasPath, "preserve this alias");

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
            {
                var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                    entry => entry.Install.PackageId == "inactive");
                scope.PublishPreparedJournal(scope.BindEligibleCandidate(row, token), token);
                return Task.FromResult(true);
            }));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.Equal("preserve this alias", File.ReadAllText(aliasPath));
        Assert.Empty(Directory.EnumerateFiles(controlPath, "prune-*.json.stage-*"));
        Assert.True(File.Exists(Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName)));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task PreparedJournalPublication_ReservesAggregatePruneBytesBeforeReadingOrStaging()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var installPath = CreateInactiveInstall(fixture.Context.RootAPath);
        var controlPath = Path.Combine(fixture.Context.RootAPath, RootMembershipRegistry.ControlDirectoryName);
        var existing = new List<string>();
        for (var index = 0; index < 4; index++)
        {
            var path = Path.Combine(controlPath, $"prune-{Guid.NewGuid():N}.json");
            using var sparse = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            sparse.SetLength(PackageStoreDeletionJournalSerializer.MaximumPayloadBytes);
            existing.Add(path);
        }

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
            {
                var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                    entry => entry.Install.PackageId == "inactive");
                scope.PublishPreparedJournal(scope.BindEligibleCandidate(row, token), token);
                return Task.FromResult(true);
            }));

        Assert.All(existing, path => Assert.Equal(PackageStoreDeletionJournalSerializer.MaximumPayloadBytes,
            new FileInfo(path).Length));
        Assert.Empty(Directory.EnumerateFiles(controlPath, "prune-*.json.stage-*"));
        Assert.True(File.Exists(Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName)));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task PreparedJournalPublication_CountsMalformedReservedNamesTowardArtifactBound()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var installPath = CreateInactiveInstall(fixture.Context.RootAPath);
        var controlPath = Path.Combine(fixture.Context.RootAPath, RootMembershipRegistry.ControlDirectoryName);
        for (var index = 0; index < PackageStoreDeletionJournalStore.MaximumPruneArtifacts; index++)
            File.WriteAllBytes(Path.Combine(controlPath, $"prune-{Guid.NewGuid():N}.json"), []);

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
            {
                var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                    entry => entry.Install.PackageId == "inactive");
                scope.PublishPreparedJournal(scope.BindEligibleCandidate(row, token), token);
                return Task.FromResult(true);
            }));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.Equal(PackageStoreDeletionJournalStore.MaximumPruneArtifacts,
            Directory.EnumerateFiles(controlPath, "prune-*").Count());
        Assert.True(File.Exists(Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName)));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task PreparedJournalPublication_RejectsAlreadyPreparedSameInstallWithoutReplacingRecord()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var installPath = CreateInactiveInstall(fixture.Context.RootAPath);
        var controlPath = Path.Combine(fixture.Context.RootAPath, RootMembershipRegistry.ControlDirectoryName);

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive");
            scope.PublishPreparedJournal(scope.BindEligibleCandidate(row, token), token);
            return Task.FromResult(true);
        });

        var journalPath = Assert.Single(Directory.EnumerateFiles(controlPath, "prune-*.json"));
        var priorBytes = File.ReadAllBytes(journalPath);
        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
            {
                var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                    entry => entry.Install.PackageId == "inactive");
                scope.PublishPreparedJournal(scope.BindEligibleCandidate(row, token), token);
                return Task.FromResult(true);
            }));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.Equal(priorBytes, File.ReadAllBytes(journalPath));
        Assert.Single(Directory.EnumerateFiles(controlPath, "prune-*.json"));
        Assert.Empty(Directory.EnumerateFiles(controlPath, "prune-*.json.stage-*"));
        Assert.True(File.Exists(Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName)));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task PreparedJournalPublication_RefusesCandidateAfterLeafPreparationBeforeCreatingStage()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var installPath = CreateInactiveInstall(fixture.Context.RootAPath);
        var controlPath = Path.Combine(fixture.Context.RootAPath, RootMembershipRegistry.ControlDirectoryName);

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive");
            var candidate = scope.BindEligibleCandidate(row, token);
            candidate.PrepareForMove(token);
            var refusal = Assert.Throws<PackageStoreAdmissionException>(() =>
                scope.PublishPreparedJournal(candidate, token));
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
            return Task.FromResult(true);
        });

        Assert.Empty(Directory.EnumerateFiles(controlPath, "prune-*"));
        Assert.True(File.Exists(Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName)));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task PreparedJournalPublication_RefusesChangedInstallTreeBeforeCreatingStage()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var installPath = CreateInactiveInstall(fixture.Context.RootAPath);
        var controlPath = Path.Combine(fixture.Context.RootAPath, RootMembershipRegistry.ControlDirectoryName);

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive");
            var candidate = scope.BindEligibleCandidate(row, token);
            var payloadPath = Path.Combine(installPath, "payload.txt");
            File.WriteAllText(payloadPath, "added after candidate binding");
            var refusal = Assert.Throws<PackageStoreAdmissionException>(() =>
                scope.PublishPreparedJournal(candidate, token));
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
            return Task.FromResult(true);
        });

        Assert.Empty(Directory.EnumerateFiles(controlPath, "prune-*"));
        Assert.True(File.Exists(Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName)));
        Assert.Equal("added after candidate binding", File.ReadAllText(Path.Combine(installPath, "payload.txt")));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task PreparedJournalPublication_RejectsForeignCandidateBeforeCreatingControlArtifacts()
    {
        using var ownerFixture = await ExecutionFixture.CreateAsync();
        using var foreignFixture = await ExecutionFixture.CreateAsync();
        CreateInactiveInstall(ownerFixture.Context.RootAPath);
        CreateInactiveInstall(foreignFixture.Context.RootAPath);
        var candidateReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowForeignCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PackageStoreDeletionCandidate? foreignCandidate = null;

        var foreignRun = foreignFixture.Service.PlanAndRunAsync("root-0", 1, 0, async (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive");
            foreignCandidate = scope.BindEligibleCandidate(row, token);
            candidateReady.SetResult();
            await allowForeignCompletion.Task.WaitAsync(token);
            return true;
        });

        try
        {
            await candidateReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await ownerFixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
            {
                Assert.NotNull(foreignCandidate);
                var refusal = Assert.Throws<PackageStoreAdmissionException>(() =>
                    scope.PublishPreparedJournal(foreignCandidate!, token));
                Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
                return Task.FromResult(true);
            });
        }
        finally
        {
            allowForeignCompletion.TrySetResult();
        }

        await foreignRun;
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(ownerFixture.Context.RootAPath,
            RootMembershipRegistry.ControlDirectoryName), "prune-*"));
        Assert.True(File.Exists(Path.Combine(ownerFixture.Context.RootAPath,
            "feed", "inactive", "1.0.0", PackageInstallStore.CompletionMarkerFileName)));
        AssertExecutionLocksReleased(ownerFixture.Context);
        AssertExecutionLocksReleased(foreignFixture.Context);
    }
}
