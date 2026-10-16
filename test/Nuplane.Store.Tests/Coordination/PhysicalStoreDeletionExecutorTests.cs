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
    public async Task DeleteEligibleCandidate_FreshNestedInstall_DeletesMarkerLastAndPreservesOtherState()
    {
        var observedPoints = new List<PackageStoreDeletionExecutionPoint>();
        Context? nativeContext = null;
        using var fixture = await ExecutionFixture.CreateAsync(
            configure: context =>
            {
                nativeContext = context;
                return Task.FromResult(Array.Empty<string>());
            },
            deletionExecutionCheckpoint: (point, journal) =>
            {
                observedPoints.Add(point);
                if (point == PackageStoreDeletionExecutionPoint.AfterDeletingPublished)
                {
                    var path = Path.Combine(nativeContext!.RootAPath, RootMembershipRegistry.ControlDirectoryName,
                        $"prune-{journal.CandidateId:N}.json");
                    Assert.Equal(PackageStoreDeletionPhase.Deleting,
                        new PackageStoreDeletionJournalSerializer().Deserialize(File.ReadAllBytes(path)).Phase);
                }
                if (point == PackageStoreDeletionExecutionPoint.AfterPayloadEntryRemoved)
                    Assert.Contains(PackageStoreDeletionExecutionPoint.AfterDeletingPublished, observedPoints);
                if (point == PackageStoreDeletionExecutionPoint.AfterFinalizingPublished)
                {
                    var path = Path.Combine(nativeContext!.RootAPath, RootMembershipRegistry.ControlDirectoryName,
                        $"prune-{journal.CandidateId:N}.json");
                    Assert.Equal(PackageStoreDeletionPhase.Finalizing,
                        new PackageStoreDeletionJournalSerializer().Deserialize(File.ReadAllBytes(path)).Phase);
                }
            });

        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath, "executor-target");
        Directory.CreateDirectory(Path.Combine(originalPath, "nested", "deeper"));
        File.WriteAllText(Path.Combine(originalPath, "nested", "one.txt"), "one");
        File.WriteAllText(Path.Combine(originalPath, "nested", "deeper", "two.txt"), "two");
        var unrelatedPath = CreateInactiveInstall(fixture.Context.RootAPath, "executor-unrelated");
        File.WriteAllText(Path.Combine(unrelatedPath, "keep.txt"), "keep");
        var stateBefore = File.ReadAllBytes(fixture.Context.SharedStatePath);

        var result = await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "executor-target");
            return Task.FromResult(scope.DeleteEligibleCandidate(row, token));
        });

        Assert.Equal(PackageStoreDeletionExecutionStatus.Deleted, result);
        Assert.False(Directory.Exists(originalPath));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(originalPath)!),
            path => Path.GetFileName(path).StartsWith(".nuplane-quarantine-", StringComparison.Ordinal));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(unrelatedPath, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(unrelatedPath, PackageInstallStore.CompletionMarkerFileName)));
        Assert.Equal(stateBefore, File.ReadAllBytes(fixture.Context.SharedStatePath));
        Assert.Contains(PackageStoreDeletionExecutionPoint.AfterDeletingPublished, observedPoints);
        Assert.Contains(PackageStoreDeletionExecutionPoint.AfterPayloadEntryRemoved, observedPoints);
        Assert.Contains(PackageStoreDeletionExecutionPoint.AfterFinalizingPublished, observedPoints);
        Assert.Contains(PackageStoreDeletionExecutionPoint.AfterMarkerRemoved, observedPoints);
        Assert.Contains(PackageStoreDeletionExecutionPoint.AfterQuarantineRootRemoved, observedPoints);
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task DeleteEligibleCandidate_OriginalSlotReplacementAfterUnlink_PreservesRemainingPayload()
    {
        var originalPath = string.Empty;
        var quarantinePath = string.Empty;
        var replaced = false;
        using var fixture = await ExecutionFixture.CreateAsync(deletionExecutionCheckpoint: (point, journal) =>
        {
            if (point != PackageStoreDeletionExecutionPoint.AfterPayloadEntryRemoved || replaced ||
                journal.OriginalInstall.PackageId != "executor-race")
                return;

            replaced = true;
            Directory.CreateDirectory(originalPath);
            File.WriteAllBytes(Path.Combine(originalPath, PackageInstallStore.CompletionMarkerFileName), []);
            File.WriteAllText(Path.Combine(originalPath, "replacement.txt"), "replacement");
        });
        originalPath = CreateInactiveInstall(fixture.Context.RootAPath, "executor-race");
        Directory.CreateDirectory(Path.Combine(originalPath, "nested"));
        File.WriteAllText(Path.Combine(originalPath, "nested", "a.txt"), "first");
        File.WriteAllText(Path.Combine(originalPath, "nested", "b.txt"), "must remain");

        var failure = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => fixture.Service.PlanAndRunAsync(
            "root-0", 1, 0, (scope, token) =>
            {
                var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                    entry => entry.Install.PackageId == "executor-race");
                return Task.FromResult(scope.DeleteEligibleCandidate(row, token));
            }));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, failure.Reason);
        Assert.True(replaced);
        Assert.True(File.Exists(Path.Combine(originalPath, "replacement.txt")));
        var quarantineNames = Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(originalPath)!)
            .Where(path => Path.GetFileName(path).StartsWith(".nuplane-quarantine-", StringComparison.Ordinal))
            .ToArray();
        quarantinePath = Assert.Single(quarantineNames);
        Assert.True(File.Exists(Path.Combine(quarantinePath, "nested", "b.txt")));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task DeleteEligibleCandidate_OriginalSlotReplacementAfterQuarantineHandleClosed_PreservesEmptyRoot()
    {
        const string packageId = "executor-root-removal-race";
        var originalPath = string.Empty;
        var quarantinePath = string.Empty;
        var replaced = false;
        using var fixture = await ExecutionFixture.CreateAsync(deletionExecutionCheckpoint: (point, journal) =>
        {
            if (point != PackageStoreDeletionExecutionPoint.AfterQuarantineHandleClosed ||
                journal.OriginalInstall.PackageId != packageId || replaced)
                return;

            replaced = true;
            Directory.CreateDirectory(originalPath);
            File.WriteAllBytes(Path.Combine(originalPath, PackageInstallStore.CompletionMarkerFileName), []);
            File.WriteAllText(Path.Combine(originalPath, "replacement.txt"), "must remain");
        });
        originalPath = CreateInactiveInstall(fixture.Context.RootAPath, packageId);
        File.WriteAllText(Path.Combine(originalPath, "payload.txt"), "deleted before root boundary");

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => DeleteOneAsync(fixture, packageId));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.True(replaced);
        quarantinePath = Assert.Single(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(originalPath)!),
            path => Path.GetFileName(path).StartsWith(".nuplane-quarantine-", StringComparison.Ordinal));
        Assert.True(Directory.Exists(quarantinePath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(quarantinePath));
        Assert.Equal("must remain", File.ReadAllText(Path.Combine(originalPath, "replacement.txt")));
        Assert.Equal(PackageStoreDeletionPhase.Finalizing,
            ReadCandidateJournal(fixture.Context.RootAPath, packageId).Phase);
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreTheory]
    [InlineData("after-quarantined")]
    [InlineData("deleting-before-unlink")]
    [InlineData("after-one-unlink")]
    [InlineData("finalizing-marker-only")]
    [InlineData("finalizing-empty-root")]
    public async Task DeleteEligibleCandidate_FreshPlanAndRunRestart_ReconcilesOneDurableBoundary(string boundary)
    {
        var stopPoint = boundary switch
        {
            "after-quarantined" => PackageStoreDeletionExecutionPoint.AfterQuarantinedPublished,
            "deleting-before-unlink" => PackageStoreDeletionExecutionPoint.AfterDeletingPublished,
            "after-one-unlink" => PackageStoreDeletionExecutionPoint.AfterPayloadEntryRemoved,
            "finalizing-marker-only" => PackageStoreDeletionExecutionPoint.AfterFinalizingPublished,
            "finalizing-empty-root" => PackageStoreDeletionExecutionPoint.AfterMarkerRemoved,
            _ => throw new ArgumentOutOfRangeException(nameof(boundary))
        };
        var packageId = $"executor-{boundary}";
        var fired = false;
        using var fixture = await ExecutionFixture.CreateAsync(deletionExecutionCheckpoint: (point, journal) =>
        {
            if (!fired && journal.OriginalInstall.PackageId == packageId && point == stopPoint)
            {
                fired = true;
                throw new IOException($"controlled stop after {point}");
            }
        });
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath, packageId);
        Directory.CreateDirectory(Path.Combine(originalPath, "nested"));
        File.WriteAllText(Path.Combine(originalPath, "nested", "one.txt"), "one");
        File.WriteAllText(Path.Combine(originalPath, "nested", "two.txt"), "two");

        IOException interruption;
        if (boundary == "after-quarantined")
        {
            interruption = await Assert.ThrowsAsync<IOException>(() => DeleteOneAsync(fixture, packageId));
        }
        else
        {
            await PrepareQuarantinedCandidateAsync(fixture, packageId);
            interruption = await Assert.ThrowsAsync<IOException>(() => DeleteOneAsync(fixture, packageId));
        }

        Assert.True(fired);
        Assert.Contains(stopPoint.ToString(), interruption.Message, StringComparison.Ordinal);
        var journal = ReadCandidateJournal(fixture.Context.RootAPath, packageId);
        var quarantinePath = Path.Combine(Path.GetDirectoryName(originalPath)!, journal.QuarantineBasename);
        AssertExecutionLocksReleased(fixture.Context);
        switch (stopPoint)
        {
            case PackageStoreDeletionExecutionPoint.AfterQuarantinedPublished:
                Assert.Equal(PackageStoreDeletionPhase.Quarantined, journal.Phase);
                Assert.Equal("one", File.ReadAllText(Path.Combine(quarantinePath, "nested", "one.txt")));
                Assert.Equal("two", File.ReadAllText(Path.Combine(quarantinePath, "nested", "two.txt")));
                break;
            case PackageStoreDeletionExecutionPoint.AfterDeletingPublished:
                Assert.Equal(PackageStoreDeletionPhase.Deleting, journal.Phase);
                Assert.Equal("one", File.ReadAllText(Path.Combine(quarantinePath, "nested", "one.txt")));
                Assert.Equal("two", File.ReadAllText(Path.Combine(quarantinePath, "nested", "two.txt")));
                break;
            case PackageStoreDeletionExecutionPoint.AfterPayloadEntryRemoved:
                Assert.Equal(PackageStoreDeletionPhase.Deleting, journal.Phase);
                Assert.Single(Directory.EnumerateFiles(Path.Combine(quarantinePath, "nested")));
                Assert.True(File.Exists(Path.Combine(quarantinePath, PackageInstallStore.CompletionMarkerFileName)));
                break;
            case PackageStoreDeletionExecutionPoint.AfterFinalizingPublished:
                Assert.Equal(PackageStoreDeletionPhase.Finalizing, journal.Phase);
                Assert.Single(Directory.EnumerateFileSystemEntries(quarantinePath));
                Assert.True(File.Exists(Path.Combine(quarantinePath, PackageInstallStore.CompletionMarkerFileName)));
                break;
            case PackageStoreDeletionExecutionPoint.AfterMarkerRemoved:
                Assert.Equal(PackageStoreDeletionPhase.Finalizing, journal.Phase);
                Assert.Empty(Directory.EnumerateFileSystemEntries(quarantinePath));
                break;
        }

        Assert.Equal(PackageStoreDeletionExecutionStatus.Deleted, await DeleteOneAsync(fixture, packageId));
        Assert.False(Directory.Exists(originalPath));
        Assert.False(Directory.Exists(quarantinePath));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task DeleteEligibleCandidate_AlreadyAbsentBeforeInvocation_DoesNotClaimPriorDeletion()
    {
        const string packageId = "executor-already-absent";
        var stopped = false;
        using var fixture = await ExecutionFixture.CreateAsync(deletionExecutionCheckpoint: (point, journal) =>
        {
            if (!stopped && journal.OriginalInstall.PackageId == packageId &&
                point == PackageStoreDeletionExecutionPoint.AfterQuarantineRootRemoved)
            {
                stopped = true;
                throw new IOException("controlled stop after quarantine-root removal");
            }
        });
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath, packageId);
        File.WriteAllText(Path.Combine(originalPath, "payload.txt"), "payload");

        await Assert.ThrowsAsync<IOException>(() => DeleteOneAsync(fixture, packageId));
        Assert.True(stopped);
        Assert.False(Directory.Exists(originalPath));
        Assert.Equal(PackageStoreDeletionPhase.Finalizing,
            ReadCandidateJournal(fixture.Context.RootAPath, packageId).Phase);

        Assert.Equal(PackageStoreDeletionExecutionStatus.AlreadyAbsent,
            await DeleteOneAsync(fixture, packageId));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreTheory]
    [InlineData("before-deleting")]
    [InlineData("after-deleting")]
    [InlineData("before-finalizing")]
    [InlineData("after-finalizing")]
    public async Task DeleteEligibleCandidate_PhasePublicationFault_PreservesExactKnownState(string boundary)
    {
        var publicationPoint = boundary switch
        {
            "before-deleting" => PackageStoreDeletionJournalPublicationPoint.BeforeDeletingPhasePublish,
            "after-deleting" => PackageStoreDeletionJournalPublicationPoint.AfterDeletingPhasePublish,
            "before-finalizing" => PackageStoreDeletionJournalPublicationPoint.BeforeFinalizingPhasePublish,
            "after-finalizing" => PackageStoreDeletionJournalPublicationPoint.AfterFinalizingPhasePublish,
            _ => throw new ArgumentOutOfRangeException(nameof(boundary))
        };
        var stopPoint = boundary switch
        {
            "after-deleting" => PackageStoreDeletionExecutionPoint.AfterDeletingPublished,
            "after-finalizing" => PackageStoreDeletionExecutionPoint.AfterFinalizingPublished,
            _ => (PackageStoreDeletionExecutionPoint?)null
        };
        const string packageId = "executor-publication-fault";
        var publicationFailed = false;
        var executionStopped = false;
        using var fixture = await ExecutionFixture.CreateAsync(
            journalPublicationCheckpoint: (point, journal) =>
            {
                if (!publicationFailed && journal.OriginalInstall.PackageId == packageId && point == publicationPoint)
                {
                    publicationFailed = true;
                    throw new IOException($"controlled {point} interruption");
                }
            },
            deletionExecutionCheckpoint: (point, journal) =>
            {
                if (!executionStopped && stopPoint == point && journal.OriginalInstall.PackageId == packageId)
                {
                    executionStopped = true;
                    throw new IOException($"controlled stop after recovered {point}");
                }
            });
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath, packageId);
        Directory.CreateDirectory(Path.Combine(originalPath, "nested"));
        File.WriteAllText(Path.Combine(originalPath, "nested", "one.txt"), "one");
        File.WriteAllText(Path.Combine(originalPath, "nested", "two.txt"), "two");
        await PrepareQuarantinedCandidateAsync(fixture, packageId);

        var failure = await Assert.ThrowsAsync<IOException>(() => DeleteOneAsync(fixture, packageId));
        Assert.Contains("controlled", failure.Message, StringComparison.Ordinal);
        Assert.True(publicationFailed);
        var journal = ReadCandidateJournal(fixture.Context.RootAPath, packageId);
        var quarantinePath = Path.Combine(Path.GetDirectoryName(originalPath)!, journal.QuarantineBasename);
        var stages = Directory.EnumerateFiles(Path.Combine(fixture.Context.RootAPath,
                RootMembershipRegistry.ControlDirectoryName), "prune-*.json.stage-*")
            .Select(path => (Path: path, Bytes: File.ReadAllBytes(path)))
            .ToArray();

        if (boundary.StartsWith("before-", StringComparison.Ordinal))
        {
            var expectedPrior = boundary == "before-deleting"
                ? PackageStoreDeletionPhase.Quarantined
                : PackageStoreDeletionPhase.Deleting;
            var expectedNext = boundary == "before-deleting"
                ? PackageStoreDeletionPhase.Deleting
                : PackageStoreDeletionPhase.Finalizing;
            Assert.Equal(expectedPrior, journal.Phase);
            var stage = Assert.Single(stages);
            Assert.Equal(expectedNext, new PackageStoreDeletionJournalSerializer().Deserialize(stage.Bytes).Phase);
            if (expectedPrior == PackageStoreDeletionPhase.Quarantined)
            {
                Assert.Equal("one", File.ReadAllText(Path.Combine(quarantinePath, "nested", "one.txt")));
                Assert.Equal("two", File.ReadAllText(Path.Combine(quarantinePath, "nested", "two.txt")));
            }
            else
                Assert.Single(Directory.EnumerateFileSystemEntries(quarantinePath));

            var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => DeleteOneAsync(fixture, packageId));
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
            Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Context.RootAPath,
                RootMembershipRegistry.ControlDirectoryName), "prune-*.json.stage-*"));
            AssertExecutionLocksReleased(fixture.Context);
            return;
        }

        Assert.Empty(stages);
        Assert.True(executionStopped);
        var expectedPublished = boundary == "after-deleting"
            ? PackageStoreDeletionPhase.Deleting
            : PackageStoreDeletionPhase.Finalizing;
        Assert.Equal(expectedPublished, journal.Phase);
        Assert.Equal(PackageStoreDeletionExecutionStatus.Deleted, await DeleteOneAsync(fixture, packageId));
        Assert.False(Directory.Exists(originalPath));
        Assert.False(Directory.Exists(quarantinePath));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task DeleteEligibleCandidate_ChangedRetentionPlan_PreservesQuarantine()
    {
        const string packageId = "executor-retention-change";
        using var fixture = await ExecutionFixture.CreateAsync();
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath, packageId, "2.0.0");
        File.WriteAllText(Path.Combine(originalPath, "payload.txt"), "protected by current budget");
        await PrepareQuarantinedCandidateAsync(fixture, packageId);
        var journal = ReadCandidateJournal(fixture.Context.RootAPath, packageId);
        var quarantinePath = Path.Combine(Path.GetDirectoryName(originalPath)!, journal.QuarantineBasename);
        var lowerVersionPath = CreateInactiveInstall(fixture.Context.RootAPath, packageId, "1.0.0");

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => fixture.Service.PlanAndRunAsync(
            "root-0", 1, 1, (scope, token) =>
            {
                var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                    entry => entry.Install == journal.OriginalInstall);
                Assert.Equal(PackageStoreRetentionClassification.Retained, row.Classification);
                Assert.Contains(PackageStoreRetentionReason.WithinInactiveRetentionBudget, row.Reasons);
                return Task.FromResult(scope.DeleteEligibleCandidate(row, token));
            }));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.Equal("protected by current budget", File.ReadAllText(Path.Combine(quarantinePath, "payload.txt")));
        Assert.True(File.Exists(Path.Combine(lowerVersionPath, PackageInstallStore.CompletionMarkerFileName)));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task DeleteEligibleCandidate_ProtectionStateChangesToProtectCandidate_PreservesQuarantine()
    {
        const string packageId = "Sample.Root";
        const string version = "2.0.0";
        using var fixture = await ExecutionFixture.CreateAsync();
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath, packageId, version);
        File.WriteAllText(Path.Combine(originalPath, "payload.txt"), "protected by new graph");
        var preview = await fixture.Inspection.InspectRootAsync("root-0", 0);
        var install = Assert.Single(preview.RetentionPlan!.Entries,
            entry => entry.Install.PackageId == packageId && entry.Install.Version == version).Install;
        Assert.Equal(PackageStoreRetentionClassification.Eligible,
            Assert.Single(preview.RetentionPlan.Entries,
                entry => entry.Install.PackageId == packageId && entry.Install.Version == version).Classification);

        var graph = fixture.Context.PrepareNextGraphPublication(install);
        await fixture.Context.Registry().PublishNativeGroupAsync(graph.Descriptor, fixture.Context.Requests,
            graph.NextState, CancellationToken.None);

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => fixture.Service.PlanAndRunAsync(
            "root-0", 1, 0, (scope, token) =>
            {
                var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                    entry => entry.Install.PackageId == packageId && entry.Install.Version == version);
                Assert.Equal(PackageStoreRetentionClassification.Retained, row.Classification);
                Assert.Contains(PackageStoreRetentionReason.ProtectedActive, row.Reasons);
                return Task.FromResult(scope.DeleteEligibleCandidate(row, token));
            }));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.Equal("protected by new graph", File.ReadAllText(Path.Combine(originalPath, "payload.txt")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Context.RootAPath,
            RootMembershipRegistry.ControlDirectoryName), "prune-*.json"));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task DeleteEligibleCandidate_UnknownCurrentInventory_PreservesQuarantine()
    {
        const string packageId = "executor-unknown-inventory";
        var unknown = false;
        using var fixture = await ExecutionFixture.CreateAsync(transform: snapshot => unknown
            ? new PackageStoreInventorySnapshot(snapshot.Root, snapshot.Epoch, false, snapshot.Entries,
                [new PackageStoreInventoryIssue("", "controlled-unknown-inventory")])
            : snapshot);
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath, packageId);
        File.WriteAllText(Path.Combine(originalPath, "payload.txt"), "inventory remains unknown");
        await PrepareQuarantinedCandidateAsync(fixture, packageId);
        var journal = ReadCandidateJournal(fixture.Context.RootAPath, packageId);
        var quarantinePath = Path.Combine(Path.GetDirectoryName(originalPath)!, journal.QuarantineBasename);
        unknown = true;

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => DeleteOneAsync(fixture, packageId));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.Equal("inventory remains unknown", File.ReadAllText(Path.Combine(quarantinePath, "payload.txt")));
        Assert.Equal(PackageStoreDeletionPhase.Quarantined,
            ReadCandidateJournal(fixture.Context.RootAPath, packageId).Phase);
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreTheory]
    [InlineData("hardlink")]
    [InlineData("symlink")]
    public async Task DeleteEligibleCandidate_LinkedBaselineEntry_IsRefusedBeforeQuarantine(string linkKind)
    {
        const string packageId = "executor-unsafe-link";
        using var fixture = await ExecutionFixture.CreateAsync();
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath, packageId);
        var payload = Path.Combine(originalPath, "payload.txt");
        var alias = Path.Combine(originalPath, "alias.txt");
        File.WriteAllText(payload, "outside alias remains");
        if (linkKind == "hardlink")
        {
            if (OperatingSystem.IsWindows())
                WindowsPhysicalStorePublicationTests.CreateHardLink(payload, alias);
            else
                UnixPhysicalStorePublicationTests.CreateHardLink(payload, alias);
        }
        else
        {
            File.CreateSymbolicLink(alias, payload);
        }

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => DeleteOneAsync(fixture, packageId));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.Equal("outside alias remains", File.ReadAllText(payload));
        Assert.True(File.Exists(alias) || File.ResolveLinkTarget(alias, returnFinalTarget: false) is not null);
        Assert.True(Directory.Exists(originalPath));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Context.RootAPath,
            RootMembershipRegistry.ControlDirectoryName), "prune-*.json"));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task DeleteEligibleCandidate_CancellationAfterDeleting_ReleasesOwnershipAndResumesFresh()
    {
        const string packageId = "executor-cancelled";
        using var cancellation = new CancellationTokenSource();
        var cancelled = false;
        using var fixture = await ExecutionFixture.CreateAsync(deletionExecutionCheckpoint: (point, journal) =>
        {
            if (!cancelled && journal.OriginalInstall.PackageId == packageId &&
                point == PackageStoreDeletionExecutionPoint.AfterDeletingPublished)
            {
                cancelled = true;
                cancellation.Cancel();
            }
        });
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath, packageId);
        File.WriteAllText(Path.Combine(originalPath, "payload.txt"), "preserved on cancellation");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeleteOneAsync(fixture, packageId,
            cancellation.Token));

        Assert.True(cancelled);
        var journal = ReadCandidateJournal(fixture.Context.RootAPath, packageId);
        Assert.Equal(PackageStoreDeletionPhase.Deleting, journal.Phase);
        var quarantinePath = Path.Combine(Path.GetDirectoryName(originalPath)!, journal.QuarantineBasename);
        Assert.Equal("preserved on cancellation", File.ReadAllText(Path.Combine(quarantinePath, "payload.txt")));
        AssertExecutionLocksReleased(fixture.Context);

        Assert.Equal(PackageStoreDeletionExecutionStatus.Deleted, await DeleteOneAsync(fixture, packageId));
        Assert.False(Directory.Exists(quarantinePath));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task DeleteEligibleCandidate_UnexpectedDescendantAfterDeleting_PreservesEveryBaselineMember()
    {
        const string packageId = "executor-added-descendant";
        string? quarantinePath = null;
        string? packageParentPath = null;
        using var fixture = await ExecutionFixture.CreateAsync(deletionExecutionCheckpoint: (point, journal) =>
        {
            if (point == PackageStoreDeletionExecutionPoint.AfterDeletingPublished &&
                journal.OriginalInstall.PackageId == packageId)
            {
                quarantinePath = Path.Combine(packageParentPath!, journal.QuarantineBasename);
                File.WriteAllText(Path.Combine(quarantinePath, "unexpected.txt"), "must remain");
            }
        });
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath, packageId);
        packageParentPath = Path.GetDirectoryName(originalPath)!;
        File.WriteAllText(Path.Combine(originalPath, "payload.txt"), "must remain");

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => DeleteOneAsync(fixture, packageId));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.NotNull(quarantinePath);
        Assert.Equal("must remain", File.ReadAllText(Path.Combine(quarantinePath!, "payload.txt")));
        Assert.Equal("must remain", File.ReadAllText(Path.Combine(quarantinePath!, "unexpected.txt")));
        Assert.Equal(PackageStoreDeletionPhase.Deleting,
            ReadCandidateJournal(fixture.Context.RootAPath, packageId).Phase);
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task DeleteEligibleCandidate_CanonicalJournalChangesAfterUnlink_PreservesLaterEntries()
    {
        const string packageId = "executor-journal-replaced";
        var changed = false;
        string? controlPath = null;
        PackageStoreDeletionJournal? journalBeforeChange = null;
        using var fixture = await ExecutionFixture.CreateAsync(deletionExecutionCheckpoint: (point, journal) =>
        {
            if (!changed && point == PackageStoreDeletionExecutionPoint.AfterPayloadEntryRemoved &&
                journal.OriginalInstall.PackageId == packageId)
            {
                changed = true;
                journalBeforeChange = journal;
                File.AppendAllText(Path.Combine(controlPath!, $"prune-{journal.CandidateId:N}.json"), "unexpected");
            }
        });
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath, packageId);
        Directory.CreateDirectory(Path.Combine(originalPath, "nested"));
        File.WriteAllText(Path.Combine(originalPath, "nested", "one.txt"), "first");
        File.WriteAllText(Path.Combine(originalPath, "nested", "two.txt"), "later entry");
        controlPath = Path.Combine(fixture.Context.RootAPath, RootMembershipRegistry.ControlDirectoryName);

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => DeleteOneAsync(fixture, packageId));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.True(changed);
        var quarantinePath = Path.Combine(Path.GetDirectoryName(originalPath)!, journalBeforeChange!.QuarantineBasename);
        Assert.True(File.Exists(Path.Combine(quarantinePath, "nested", "two.txt")));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecuteEligibleCandidates_PreservesFirstDeletionWhenSecondCancels()
    {
        const string firstId = "outcome-a";
        const string secondId = "outcome-b";
        const string laterId = "outcome-c";
        using var cancellation = new CancellationTokenSource();
        var cancelled = false;
        using var fixture = await ExecutionFixture.CreateAsync(deletionExecutionCheckpoint: (point, journal) =>
        {
            if (!cancelled && journal.OriginalInstall.PackageId == secondId &&
                point == PackageStoreDeletionExecutionPoint.AfterDeletingPublished)
            {
                cancelled = true;
                cancellation.Cancel();
            }
        });
        var firstPath = CreateInactiveInstall(fixture.Context.RootAPath, firstId);
        File.WriteAllText(Path.Combine(firstPath, "payload.txt"), "first");
        var secondPath = CreateInactiveInstall(fixture.Context.RootAPath, secondId);
        File.WriteAllText(Path.Combine(secondPath, "payload.txt"), "second");
        var laterPath = CreateInactiveInstall(fixture.Context.RootAPath, laterId);
        File.WriteAllText(Path.Combine(laterPath, "payload.txt"), "later");

        var outcome = await fixture.Service.ExecuteEligibleCandidatesAsync("root-0", 1, 0, cancellation.Token);

        Assert.True(cancelled);
        Assert.Equal(PackageStoreExecutionRequestStatus.Cancelled, outcome.Status);
        var first = OutcomeFor(outcome, firstId);
        Assert.Equal(PackageStoreExecutionCandidateAttempt.Completed, first.Attempt);
        Assert.Equal(PackageStoreExecutionPhysicalOutcome.Deleted, first.PhysicalOutcome);
        var second = OutcomeFor(outcome, secondId);
        Assert.Equal(PackageStoreExecutionCandidateAttempt.Cancelled, second.Attempt);
        Assert.Equal(PackageStoreExecutionPhysicalOutcome.Quarantined, second.PhysicalOutcome);
        var later = OutcomeFor(outcome, laterId);
        Assert.Equal(PackageStoreExecutionCandidateAttempt.NotAttempted, later.Attempt);
        Assert.Null(later.PhysicalOutcome);

        Assert.False(Directory.Exists(firstPath));
        Assert.True(Directory.Exists(laterPath));
        Assert.Equal("later", File.ReadAllText(Path.Combine(laterPath, "payload.txt")));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecuteEligibleCandidates_PreservesFirstDeletionWhenSecondRemovalThrows()
    {
        const string firstId = "outcome-io-a";
        const string secondId = "outcome-io-b";
        const string laterId = "outcome-io-c";
        var native = OperatingSystem.IsWindows()
            ? (IPhysicalStoreFileSystem)new WindowsPhysicalStoreFileSystem()
            : new UnixPhysicalStoreFileSystem();
        var faulting = new RemoveThenThrowFileSystem(native, "second.txt");
        using var fixture = await ExecutionFixture.CreateAsync(fileSystem: faulting);
        var firstPath = CreateInactiveInstall(fixture.Context.RootAPath, firstId);
        File.WriteAllText(Path.Combine(firstPath, "first.txt"), "first");
        var secondPath = CreateInactiveInstall(fixture.Context.RootAPath, secondId);
        File.WriteAllText(Path.Combine(secondPath, "second.txt"), "removed before failure");
        File.WriteAllText(Path.Combine(secondPath, "survivor.txt"), "survivor");
        var laterPath = CreateInactiveInstall(fixture.Context.RootAPath, laterId);
        File.WriteAllText(Path.Combine(laterPath, "later.txt"), "later");

        var outcome = await fixture.Service.ExecuteEligibleCandidatesAsync("root-0", 1, 0);

        Assert.Equal(PackageStoreExecutionRequestStatus.Failed, outcome.Status);
        var first = OutcomeFor(outcome, firstId);
        Assert.Equal(PackageStoreExecutionCandidateAttempt.Completed, first.Attempt);
        Assert.Equal(PackageStoreExecutionPhysicalOutcome.Deleted, first.PhysicalOutcome);
        var second = OutcomeFor(outcome, secondId);
        Assert.Equal(PackageStoreExecutionCandidateAttempt.Failed, second.Attempt);
        Assert.Equal(PackageStoreExecutionPhysicalOutcome.PartiallyRemoved, second.PhysicalOutcome);
        Assert.Contains("second.txt", second.MissingAtLastObservation!.Value);
        var later = OutcomeFor(outcome, laterId);
        Assert.Equal(PackageStoreExecutionCandidateAttempt.NotAttempted, later.Attempt);
        Assert.Null(later.PhysicalOutcome);

        Assert.False(Directory.Exists(firstPath));
        var journal = ReadCandidateJournal(fixture.Context.RootAPath, secondId);
        var quarantinePath = Path.Combine(Path.GetDirectoryName(secondPath)!, journal.QuarantineBasename);
        Assert.False(File.Exists(Path.Combine(quarantinePath, "second.txt")));
        Assert.Equal("survivor", File.ReadAllText(Path.Combine(quarantinePath, "survivor.txt")));
        Assert.True(File.Exists(Path.Combine(quarantinePath, PackageInstallStore.CompletionMarkerFileName)));
        Assert.Equal("later", File.ReadAllText(Path.Combine(laterPath, "later.txt")));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecuteEligibleCandidates_BeforeAdmissionCancellationRemainsCancellation()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.ExecuteEligibleCandidatesAsync("root-0", 1, 0, cancellation.Token));

        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecuteEligibleCandidates_EpochMismatchRemainsTypedRefusal()
    {
        const string packageId = "outcome-epoch-mismatch";
        using var fixture = await ExecutionFixture.CreateAsync();
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath, packageId);
        File.WriteAllText(Path.Combine(originalPath, "payload.txt"), "payload");

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            fixture.Service.ExecuteEligibleCandidatesAsync("root-0", 2, 0));

        Assert.Equal(PackageStoreAdmissionReason.RootMismatch, refusal.Reason);
        Assert.Equal("payload", File.ReadAllText(Path.Combine(originalPath, "payload.txt")));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecuteEligibleCandidates_QuarantineCheckpointFailureReportsVerifiedQuarantine()
    {
        const string packageId = "outcome-quarantine-checkpoint";
        var interrupted = false;
        using var fixture = await ExecutionFixture.CreateAsync(deletionExecutionCheckpoint: (point, journal) =>
        {
            if (!interrupted && journal.OriginalInstall.PackageId == packageId &&
                point == PackageStoreDeletionExecutionPoint.AfterQuarantinedPublished)
            {
                interrupted = true;
                throw new IOException("controlled stop after Quarantined publication");
            }
        });
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath, packageId);
        File.WriteAllText(Path.Combine(originalPath, "payload.txt"), "payload");

        var outcome = await fixture.Service.ExecuteEligibleCandidatesAsync("root-0", 1, 0);

        Assert.True(interrupted);
        Assert.Equal(PackageStoreExecutionRequestStatus.Failed, outcome.Status);
        var candidate = OutcomeFor(outcome, packageId);
        Assert.Equal(PackageStoreExecutionCandidateAttempt.Failed, candidate.Attempt);
        Assert.Equal(PackageStoreExecutionPhysicalOutcome.Quarantined, candidate.PhysicalOutcome);
        Assert.Equal(PackageStoreDeletionPhase.Quarantined, candidate.JournalPhase);
        Assert.Empty(candidate.MissingAtLastObservation!.Value);
        var journal = ReadCandidateJournal(fixture.Context.RootAPath, packageId);
        var quarantinePath = Path.Combine(Path.GetDirectoryName(originalPath)!, journal.QuarantineBasename);
        Assert.False(Directory.Exists(originalPath));
        Assert.Equal("payload", File.ReadAllText(Path.Combine(quarantinePath, "payload.txt")));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteEligibleCandidates_VerifiesReturnedRootRemovalBeforeReportingDeleted(bool cancel)
    {
        const string packageId = "outcome-root-verified";
        using var cancellation = new CancellationTokenSource();
        var interrupted = false;
        using var fixture = await ExecutionFixture.CreateAsync(deletionExecutionCheckpoint: (point, journal) =>
        {
            if (interrupted || journal.OriginalInstall.PackageId != packageId ||
                point != PackageStoreDeletionExecutionPoint.AfterQuarantineRootRemoved)
                return;
            interrupted = true;
            if (cancel)
                cancellation.Cancel();
            else
                throw new IOException("controlled stop after quarantine-root removal");
        });
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath, packageId);
        File.WriteAllText(Path.Combine(originalPath, "payload.txt"), "payload");

        var outcome = await fixture.Service.ExecuteEligibleCandidatesAsync("root-0", 1, 0, cancellation.Token);

        Assert.True(interrupted);
        Assert.Equal(cancel ? PackageStoreExecutionRequestStatus.Cancelled : PackageStoreExecutionRequestStatus.Failed,
            outcome.Status);
        var candidate = OutcomeFor(outcome, packageId);
        Assert.Equal(cancel ? PackageStoreExecutionCandidateAttempt.Cancelled : PackageStoreExecutionCandidateAttempt.Failed,
            candidate.Attempt);
        Assert.Equal(PackageStoreExecutionPhysicalOutcome.Deleted, candidate.PhysicalOutcome);
        Assert.False(Directory.Exists(originalPath));
        AssertExecutionLocksReleased(fixture.Context);

        var fresh = await fixture.Service.ExecuteEligibleCandidatesAsync("root-0", 1, 0);
        Assert.Equal(PackageStoreExecutionRequestStatus.Completed, fresh.Status);
        Assert.Equal(PackageStoreExecutionPhysicalOutcome.AlreadyAbsent, OutcomeFor(fresh, packageId).PhysicalOutcome);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecuteEligibleCandidates_PayloadRemoveThenThrowReportsObservedPartialState()
    {
        const string packageId = "outcome-payload-uncertain";
        var native = OperatingSystem.IsWindows()
            ? (IPhysicalStoreFileSystem)new WindowsPhysicalStoreFileSystem()
            : new UnixPhysicalStoreFileSystem();
        var faulting = new RemoveThenThrowFileSystem(native, "one.txt");
        using var fixture = await ExecutionFixture.CreateAsync(fileSystem: faulting);
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath, packageId);
        Directory.CreateDirectory(Path.Combine(originalPath, "nested"));
        File.WriteAllText(Path.Combine(originalPath, "nested", "one.txt"), "removed before exception");
        File.WriteAllText(Path.Combine(originalPath, "nested", "two.txt"), "survives");

        var outcome = await fixture.Service.ExecuteEligibleCandidatesAsync("root-0", 1, 0);

        Assert.Equal(PackageStoreExecutionRequestStatus.Failed, outcome.Status);
        var candidate = OutcomeFor(outcome, packageId);
        Assert.Equal(PackageStoreExecutionCandidateAttempt.Failed, candidate.Attempt);
        Assert.Equal(PackageStoreExecutionPhysicalOutcome.PartiallyRemoved, candidate.PhysicalOutcome);
        Assert.NotNull(candidate.MissingAtStart);
        Assert.Empty(candidate.MissingAtStart!.Value);
        Assert.Contains("nested/one.txt", candidate.MissingAtLastObservation!.Value);
        var journal = ReadCandidateJournal(fixture.Context.RootAPath, packageId);
        var quarantinePath = Path.Combine(Path.GetDirectoryName(originalPath)!, journal.QuarantineBasename);
        Assert.False(File.Exists(Path.Combine(quarantinePath, "nested", "one.txt")));
        Assert.Equal("survives", File.ReadAllText(Path.Combine(quarantinePath, "nested", "two.txt")));
        Assert.True(File.Exists(Path.Combine(quarantinePath, PackageInstallStore.CompletionMarkerFileName)));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecuteEligibleCandidates_RootRemoveThenThrowDoesNotAttributeDeletion()
    {
        const string packageId = "outcome-root-uncertain";
        var native = OperatingSystem.IsWindows()
            ? (IPhysicalStoreFileSystem)new WindowsPhysicalStoreFileSystem()
            : new UnixPhysicalStoreFileSystem();
        var faulting = new RemoveThenThrowFileSystem(native, throwAfterQuarantineRootRemoval: true);
        using var fixture = await ExecutionFixture.CreateAsync(fileSystem: faulting);
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath, packageId);
        File.WriteAllText(Path.Combine(originalPath, "payload.txt"), "payload");

        var outcome = await fixture.Service.ExecuteEligibleCandidatesAsync("root-0", 1, 0);

        Assert.Equal(PackageStoreExecutionRequestStatus.Failed, outcome.Status);
        var candidate = OutcomeFor(outcome, packageId);
        Assert.Equal(PackageStoreExecutionCandidateAttempt.Failed, candidate.Attempt);
        Assert.Equal(PackageStoreExecutionPhysicalOutcome.QuarantineRootAbsentUnattributed, candidate.PhysicalOutcome);
        Assert.NotEqual(PackageStoreExecutionPhysicalOutcome.Deleted, candidate.PhysicalOutcome);
        Assert.NotEqual(PackageStoreExecutionPhysicalOutcome.AlreadyAbsent, candidate.PhysicalOutcome);
        Assert.False(Directory.Exists(originalPath));
        AssertExecutionLocksReleased(fixture.Context);

        var fresh = await fixture.Service.ExecuteEligibleCandidatesAsync("root-0", 1, 0);
        Assert.Equal(PackageStoreExecutionPhysicalOutcome.AlreadyAbsent, OutcomeFor(fresh, packageId).PhysicalOutcome);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecuteEligibleCandidates_PostCallbackCatalogRefusalPreservesVerifiedDeletion()
    {
        const string packageId = "outcome-post-callback-refusal";
        Context? nativeContext = null;
        using var fixture = await ExecutionFixture.CreateAsync(
            configure: context =>
            {
                nativeContext = context;
                return Task.FromResult(Array.Empty<string>());
            },
            deletionExecutionCheckpoint: (point, journal) =>
            {
                if (point == PackageStoreDeletionExecutionPoint.AfterQuarantineRootRemoved &&
                    journal.OriginalInstall.PackageId == packageId)
                    File.WriteAllText(nativeContext!.SharedStatePath, "corrupt-after-candidate");
            });
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath, packageId);
        File.WriteAllText(Path.Combine(originalPath, "payload.txt"), "payload");

        var outcome = await fixture.Service.ExecuteEligibleCandidatesAsync("root-0", 1, 0);

        Assert.Equal(PackageStoreExecutionRequestStatus.Failed, outcome.Status);
        Assert.IsType<PackageStoreAdmissionException>(outcome.Diagnostic);
        var candidate = OutcomeFor(outcome, packageId);
        Assert.Equal(PackageStoreExecutionCandidateAttempt.Completed, candidate.Attempt);
        Assert.Equal(PackageStoreExecutionPhysicalOutcome.Deleted, candidate.PhysicalOutcome);
        Assert.False(Directory.Exists(originalPath));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecuteEligibleCandidates_OwnerCloseFailurePreservesVerifiedDeletion()
    {
        const string packageId = "outcome-owner-close-failure";
        var native = OperatingSystem.IsWindows()
            ? (IPhysicalStoreFileSystem)new WindowsPhysicalStoreFileSystem()
            : new UnixPhysicalStoreFileSystem();
        var faulting = new LockReleaseFaultFileSystem(native);
        using var fixture = await ExecutionFixture.CreateAsync(fileSystem: faulting);
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath, packageId);
        File.WriteAllText(Path.Combine(originalPath, "payload.txt"), "payload");
        faulting.FailNextRelease();

        var outcome = await fixture.Service.ExecuteEligibleCandidatesAsync("root-0", 1, 0);

        Assert.Equal(PackageStoreExecutionRequestStatus.Failed, outcome.Status);
        Assert.Contains("release failure after releasing the lock", outcome.Diagnostic!.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, fixture.Inventory.ReadCalls);
        var candidate = OutcomeFor(outcome, packageId);
        Assert.Equal(PackageStoreExecutionCandidateAttempt.Completed, candidate.Attempt);
        Assert.Equal(PackageStoreExecutionPhysicalOutcome.Deleted, candidate.PhysicalOutcome);
        Assert.False(Directory.Exists(originalPath));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task DeleteEligibleCandidate_NativeFileRemovalCompletesThenThrows_FreshOwnerReconcilesAbsence()
    {
        const string packageId = "executor-remove-then-throw";
        var native = OperatingSystem.IsWindows()
            ? (IPhysicalStoreFileSystem)new WindowsPhysicalStoreFileSystem()
            : new UnixPhysicalStoreFileSystem();
        var faulting = new RemoveThenThrowFileSystem(native, "one.txt");
        using var fixture = await ExecutionFixture.CreateAsync(fileSystem: faulting);
        var originalPath = CreateInactiveInstall(fixture.Context.RootAPath, packageId);
        Directory.CreateDirectory(Path.Combine(originalPath, "nested"));
        File.WriteAllText(Path.Combine(originalPath, "nested", "one.txt"), "removed before exception");
        File.WriteAllText(Path.Combine(originalPath, "nested", "two.txt"), "survives first invocation");

        var failure = await Assert.ThrowsAsync<IOException>(() => DeleteOneAsync(fixture, packageId));

        Assert.Contains("after native removal", failure.Message, StringComparison.Ordinal);
        var journal = ReadCandidateJournal(fixture.Context.RootAPath, packageId);
        Assert.Equal(PackageStoreDeletionPhase.Deleting, journal.Phase);
        var quarantinePath = Path.Combine(Path.GetDirectoryName(originalPath)!, journal.QuarantineBasename);
        Assert.False(File.Exists(Path.Combine(quarantinePath, "nested", "one.txt")));
        Assert.Equal("survives first invocation", File.ReadAllText(Path.Combine(quarantinePath, "nested", "two.txt")));
        Assert.True(File.Exists(Path.Combine(quarantinePath, PackageInstallStore.CompletionMarkerFileName)));
        AssertExecutionLocksReleased(fixture.Context);

        Assert.Equal(PackageStoreDeletionExecutionStatus.Deleted, await DeleteOneAsync(fixture, packageId));
        Assert.False(Directory.Exists(quarantinePath));
        AssertExecutionLocksReleased(fixture.Context);
    }

    private sealed class RemoveThenThrowFileSystem(
        IPhysicalStoreFileSystem inner,
        string? faultingBasename = null,
        bool throwAfterQuarantineRootRemoval = false)
        : DelegatingMaintenanceFileSystem(inner)
    {
        private int _thrown;

        public override void RemoveFileAt(PhysicalStoreDirectoryHandle parent, string singleName,
            PhysicalFileIdentity expectedIdentity)
        {
            base.RemoveFileAt(parent, singleName, expectedIdentity);
            if (singleName == faultingBasename && Interlocked.Exchange(ref _thrown, 1) == 0)
                throw new IOException("controlled failure after native removal");
        }

        public override void RemoveEmptyDirectoryAt(PhysicalStoreDirectoryHandle parent, string singleName,
            PhysicalFileIdentity expectedIdentity)
        {
            base.RemoveEmptyDirectoryAt(parent, singleName, expectedIdentity);
            if (throwAfterQuarantineRootRemoval &&
                singleName.StartsWith(".nuplane-quarantine-", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _thrown, 1) == 0)
                throw new IOException("controlled failure after quarantine-root removal");
        }
    }

    private sealed class LockReleaseFaultFileSystem(IPhysicalStoreFileSystem inner)
        : DelegatingMaintenanceFileSystem(inner)
    {
        private int _failNextRelease;

        internal void FailNextRelease() => Interlocked.Exchange(ref _failNextRelease, 1);

        public override async ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
        {
            var owner = await base.TryAcquireExclusiveLock(file).ConfigureAwait(false);
            return owner is null ? null : new FaultAfterReleaseOwner(owner, this);
        }

        private bool ShouldFailRelease() => Interlocked.Exchange(ref _failNextRelease, 0) == 1;

        private sealed class FaultAfterReleaseOwner(IAsyncDisposable inner, LockReleaseFaultFileSystem parent)
            : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync().ConfigureAwait(false);
                if (parent.ShouldFailRelease())
                    throw new IOException("Injected native lock release failure after releasing the lock.");
            }
        }
    }

    private abstract class DelegatingMaintenanceFileSystem(IPhysicalStoreFileSystem inner)
        : DelegatingDeletionNativeFileSystem(inner), IPhysicalStorePublicationFileSystem,
            IPhysicalStoreDirectoryPublicationFileSystem, IPhysicalStoreControlRecoveryFileSystem
    {
        private readonly IPhysicalStorePublicationFileSystem _publication = (IPhysicalStorePublicationFileSystem)inner;
        private readonly IPhysicalStoreDirectoryPublicationFileSystem _directoryPublication =
            (IPhysicalStoreDirectoryPublicationFileSystem)inner;
        private readonly IPhysicalStoreControlRecoveryFileSystem _controlRecovery =
            (IPhysicalStoreControlRecoveryFileSystem)inner;

        public PhysicalStoreEntryInfo PublishControlFileAt(PhysicalStoreDirectoryHandle parent, string stagedName,
            PhysicalFileIdentity expectedStagedIdentity, string destinationName,
            PhysicalFileIdentity? expectedDestinationIdentity)
            => _publication.PublishControlFileAt(parent, stagedName, expectedStagedIdentity, destinationName,
                expectedDestinationIdentity);
        public void RemoveControlFileAt(PhysicalStoreDirectoryHandle parent, string singleName,
            PhysicalFileIdentity expectedIdentity)
            => _publication.RemoveControlFileAt(parent, singleName, expectedIdentity);
        public PhysicalStoreEntryInfo PublishDirectoryNoReplaceAt(PhysicalStoreDirectoryHandle parent,
            string stagedName, PhysicalFileIdentity expectedStagedIdentity, string destinationName)
            => _directoryPublication.PublishDirectoryNoReplaceAt(parent, stagedName, expectedStagedIdentity,
                destinationName);
        public ValueTask<PhysicalStoreLockedControlFile?> TryOpenAndLockControlFileForRemovalAt(
            PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedIdentity)
            => _controlRecovery.TryOpenAndLockControlFileForRemovalAt(parent, singleName, expectedIdentity);
        public ValueTask RemoveLockedControlFileAsync(PhysicalStoreLockedControlFile file)
            => _controlRecovery.RemoveLockedControlFileAsync(file);
        public PhysicalStoreEntryInfo InspectLockedControlFile(PhysicalStoreLockedControlFile file)
            => _controlRecovery.InspectLockedControlFile(file);
        public byte[] ReadLockedControlFile(PhysicalStoreLockedControlFile file, int maximumBytes)
            => _controlRecovery.ReadLockedControlFile(file, maximumBytes);
        public PhysicalStoreEntryInfo MoveControlFileNoReplaceAt(PhysicalStoreDirectoryHandle parent,
            string sourceName, PhysicalFileIdentity expectedSourceIdentity, string destinationName)
            => _controlRecovery.MoveControlFileNoReplaceAt(parent, sourceName, expectedSourceIdentity,
                destinationName);
    }

    private static PackageStoreExecutionCandidateOutcome OutcomeFor(
        PackageStoreExecutionOutcome outcome,
        string packageId)
        => Assert.Single(outcome.Candidates, candidate => candidate.Install.PackageId == packageId);

    private static Task<PackageStoreDeletionExecutionStatus> DeleteOneAsync(
        ExecutionFixture fixture,
        string packageId,
        CancellationToken operationCancellationToken = default)
        => fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == packageId);
            return Task.FromResult(scope.DeleteEligibleCandidate(row, token));
        }, operationCancellationToken);

    private static Task PrepareQuarantinedCandidateAsync(ExecutionFixture fixture, string packageId)
        => fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == packageId);
            var candidate = scope.BindEligibleCandidate(row, token);
            scope.PublishPreparedJournal(candidate, token);
            scope.QuarantinePreparedCandidate(candidate, token);
            return Task.FromResult(true);
        });

    private static PackageStoreDeletionJournal ReadCandidateJournal(string rootPath, string packageId)
    {
        var controlPath = Path.Combine(rootPath, RootMembershipRegistry.ControlDirectoryName);
        return Assert.Single(Directory.EnumerateFiles(controlPath, "prune-*.json")
            .Select(path => new PackageStoreDeletionJournalSerializer().Deserialize(File.ReadAllBytes(path))),
            journal => journal.OriginalInstall.PackageId == packageId);
    }
}
