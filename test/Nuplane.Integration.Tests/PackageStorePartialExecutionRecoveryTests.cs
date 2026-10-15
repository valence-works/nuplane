using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Integration.Tests.Fixtures;
using Nuplane.Loading;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.DeletionRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.Maintenance;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Integration.Tests;

public sealed class PackageStorePartialExecutionRecoveryTests
{
    private const long EnrollmentEpoch = 37;
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("AfterQuarantinedPublished", "Quarantined")]
    [InlineData("AfterDeletingPublished", "Deleting")]
    [InlineData("AfterPayloadEntryRemoved", "Deleting")]
    [InlineData("AfterFinalizingPublished", "Finalizing")]
    [InlineData("AfterMarkerRemoved", "Finalizing")]
    [InlineData("AfterQuarantineHandleClosed", "Finalizing")]
    [InlineData("AfterQuarantineRootRemoved", "Finalizing")]
    public async Task ExecuteEligibleCandidatesAsync_WhenInterruptedAtDeletionCheckpoint_RestartReportsVerifiedFacts(
        string checkpointPoint,
        string expectedPhase)
    {
        using var fixture = await OverlappingPackageGraphProtectionTests.GraphUseFixture.CreateAsync(
            PackageLoadMode.HostIntegrated);
        var preflight = await InspectAsync(fixture);
        var eligible = EligibleRows(preflight).First();
        var eligiblePackage = eligible.Install.PackageId;
        var eligibleVersion = eligible.Install.Version;
        var packages = await PersistedProtectedPackagesAsync(fixture, preflight);
        var stateBefore = await CaptureStateEvidenceAsync(fixture);
        var protectedBefore = CapturePackageEvidence(fixture, packages);
        await AssertOfflineMemberProtectionAsync(fixture, preflight);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var interrupted = await StartPausedExecutionAsync(fixture,
            stopAtDeletionPoint: checkpointPoint, cancellationToken: deadline.Token);
        var checkpoint = interrupted.Checkpoint;
        Assert.Equal(checkpointPoint, String(checkpoint, "point"));
        Assert.Equal(expectedPhase, String(checkpoint, "journalPhase"));
        Assert.Equal(eligiblePackage, String(checkpoint, "packageId"));
        Assert.Equal(eligibleVersion, String(checkpoint, "version"));

        var killed = await interrupted.Process.TerminateAsync(deadline.Token);
        Assert.NotEqual(0, killed.ExitCode);

        var journal = DiscoverSingleJournal(fixture);
        Assert.Equal(expectedPhase, journal.Journal.Phase.ToString());
        Assert.Equal(String(checkpoint, "candidateId"), journal.Journal.CandidateId.ToString("N"));
        AssertCheckpointNamespace(fixture, journal.Journal, checkpointPoint);
        var candidateNamespaceBeforeAdmission = CaptureCandidateNamespace(fixture, journal.Journal);
        var journalPayloadBeforeAdmission = journal.Payload.ToArray();
        var journalIdentityBeforeAdmission = journal.FileIdentity;

        await AssertOrdinaryAdmissionDoesNotResumeAsync(fixture, deadline.Token);

        journal = DiscoverSingleJournal(fixture);
        Assert.Equal(journalPayloadBeforeAdmission, journal.Payload);
        Assert.Equal(journalIdentityBeforeAdmission, journal.FileIdentity);
        AssertCandidateNamespaceEqual(candidateNamespaceBeforeAdmission,
            CaptureCandidateNamespace(fixture, journal.Journal));
        AssertStateEvidenceEqual(stateBefore, await CaptureStateEvidenceAsync(fixture));
        AssertPackageEvidenceEqual(protectedBefore, CapturePackageEvidence(fixture, packages));

        var resumed = await RunExecutionAsync(fixture, cancellationToken: deadline.Token);
        Assert.Equal("execution-result", resumed.Kind);
        Assert.Equal("Completed", String(resumed.Message, "status"));
        var resumedCandidate = Candidate(resumed.Message, eligiblePackage, eligibleVersion);
        Assert.Equal("Eligible", String(resumedCandidate, "classification"));
        Assert.Equal("Completed", String(resumedCandidate, "attempt"));
        Assert.Equal(checkpointPoint == "AfterQuarantineRootRemoved" ? "AlreadyAbsent" : "Deleted",
            String(resumedCandidate, "physicalOutcome"));

        AssertOfflineMemberOutcome(resumed.Message);
        AssertStateEvidenceEqual(stateBefore, await CaptureStateEvidenceAsync(fixture));
        AssertPackageEvidenceEqual(protectedBefore, CapturePackageEvidence(fixture, packages));
        AssertInstallAbsent(fixture, eligiblePackage, eligibleVersion);
    }

    [Fact]
    public async Task ExecuteEligibleCandidatesAsync_WhenInterruptedAfterPreparedPublication_RestartResumesExactCandidate()
    {
        using var fixture = await OverlappingPackageGraphProtectionTests.GraphUseFixture.CreateAsync(
            PackageLoadMode.HostIntegrated);
        var preflight = await InspectAsync(fixture);
        var eligible = EligibleRows(preflight).First();
        var packages = await PersistedProtectedPackagesAsync(fixture, preflight);
        var stateBefore = await CaptureStateEvidenceAsync(fixture);
        var protectedBefore = CapturePackageEvidence(fixture, packages);
        await AssertOfflineMemberProtectionAsync(fixture, preflight);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var interrupted = await StartPausedExecutionAsync(fixture,
            stopAtPublicationPoint: "AfterNativePublish", cancellationToken: deadline.Token);
        Assert.Equal("Prepared", String(interrupted.Checkpoint, "journalPhase"));
        Assert.Equal(eligible.Install.PackageId, String(interrupted.Checkpoint, "packageId"));
        Assert.Equal(eligible.Install.Version, String(interrupted.Checkpoint, "version"));

        var killed = await interrupted.Process.TerminateAsync(deadline.Token);
        Assert.NotEqual(0, killed.ExitCode);
        var journal = DiscoverSingleJournal(fixture);
        Assert.Equal(PackageStoreDeletionPhase.Prepared, journal.Journal.Phase);
        AssertCheckpointNamespace(fixture, journal.Journal, "Prepared");
        var candidateNamespaceBeforeAdmission = CaptureCandidateNamespace(fixture, journal.Journal);
        var journalPayloadBeforeAdmission = journal.Payload.ToArray();

        await AssertOrdinaryAdmissionDoesNotResumeAsync(fixture, deadline.Token);
        journal = DiscoverSingleJournal(fixture);
        Assert.Equal(journalPayloadBeforeAdmission, journal.Payload);
        AssertCandidateNamespaceEqual(candidateNamespaceBeforeAdmission,
            CaptureCandidateNamespace(fixture, journal.Journal));

        var resumed = await RunExecutionAsync(fixture, cancellationToken: deadline.Token);
        Assert.Equal("execution-result", resumed.Kind);
        Assert.Equal("Completed", String(resumed.Message, "status"));
        Assert.Equal("Deleted", String(Candidate(resumed.Message, eligible.Install.PackageId,
            eligible.Install.Version), "physicalOutcome"));
        AssertOfflineMemberOutcome(resumed.Message);
        AssertStateEvidenceEqual(stateBefore, await CaptureStateEvidenceAsync(fixture));
        AssertPackageEvidenceEqual(protectedBefore, CapturePackageEvidence(fixture, packages));
        AssertInstallAbsent(fixture, eligible.Install.PackageId, eligible.Install.Version);
    }

    [Fact]
    public async Task ExecuteEligibleCandidatesAsync_WhenOfflineMemberHasHistoricalLkgOnlyGraph_RecoversDeletionAndPreservesHistory()
    {
        using var fixture = await OverlappingPackageGraphProtectionTests.GraphUseFixture.CreateAsync(
            PackageLoadMode.HostIntegrated);
        var serializer = new StoreStateSerializer();
        var memberBState = await serializer.LoadAsync(fixture.StatePaths["member-b"], CancellationToken.None);
        var historicalState = CreateHistoricalLkgOnlyMemberState(fixture, memberBState);
        await fixture.PublishProtectedStateAsync("member-b", historicalState);

        var publishedState = await serializer.LoadAsync(fixture.StatePaths["member-b"], CancellationToken.None);
        Assert.Equal(ProtectionDigest.StateBody(publishedState), publishedState.ProtectionRecord!.StateBodyDigest);
        Assert.Equal(ProtectionDigest.Protection(publishedState.ProtectionRecord),
            publishedState.ProtectionRecord.ProtectionDigest);

        var preflight = await InspectAsync(fixture);
        var historicalRoot = await AssertOfflineHistoricalLkgOnlyProtectionAsync(fixture, preflight);
        var historicalGraphPackages = fixture.Graphs["member-b"].Packages;
        var historicalGraphEvidence = CapturePackageEvidence(fixture, historicalGraphPackages);
        var protectedPackages = await PersistedProtectedPackagesAsync(fixture, preflight);
        var protectedEvidence = CapturePackageEvidence(fixture, protectedPackages);
        var stateEvidence = await CaptureStateEvidenceAsync(fixture);

        var eligible = EligibleRows(preflight).ToArray();
        var unrelated = Assert.Single(eligible);
        Assert.Equal("Unrelated.Package", unrelated.Install.PackageId);
        Assert.Equal("0.1.0", unrelated.Install.Version);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var interrupted = await StartPausedExecutionAsync(fixture,
            stopAtDeletionPoint: "AfterPayloadEntryRemoved", cancellationToken: deadline.Token);
        Assert.Equal("Unrelated.Package", String(interrupted.Checkpoint, "packageId"));
        Assert.Equal("0.1.0", String(interrupted.Checkpoint, "version"));
        Assert.NotEqual(0, (await interrupted.Process.TerminateAsync(deadline.Token)).ExitCode);

        var journal = DiscoverSingleJournal(fixture);
        Assert.Equal(PackageStoreDeletionPhase.Deleting, journal.Journal.Phase);
        AssertCheckpointNamespace(fixture, journal.Journal, "AfterPayloadEntryRemoved");
        AssertStateEvidenceEqual(stateEvidence, await CaptureStateEvidenceAsync(fixture));
        AssertPackageEvidenceEqual(protectedEvidence, CapturePackageEvidence(fixture, protectedPackages));
        AssertPackageEvidenceEqual(historicalGraphEvidence,
            CapturePackageEvidence(fixture, historicalGraphPackages));
        AssertInstallIdentityPresent(fixture, historicalRoot);

        // A new owned child recovers the partial deletion, then another fresh child proves the
        // completed candidate is AlreadyAbsent while the offline historical graph remains protected.
        var resumed = await RunExecutionAsync(fixture, cancellationToken: deadline.Token);
        Assert.Equal("execution-result", resumed.Kind);
        Assert.Equal("Completed", String(resumed.Message, "status"));
        var deleted = Candidate(resumed.Message, "Unrelated.Package", "0.1.0");
        Assert.Equal("Eligible", String(deleted, "classification"));
        Assert.Equal("Completed", String(deleted, "attempt"));
        Assert.Equal("Deleted", String(deleted, "physicalOutcome"));
        AssertHistoricalOfflineMemberOutcome(resumed.Message);
        AssertInstallAbsent(fixture, "Unrelated.Package", "0.1.0");

        var afterRecovery = await InspectAsync(fixture);
        var retainedHistoricalRoot = await AssertOfflineHistoricalLkgOnlyProtectionAsync(fixture, afterRecovery);
        Assert.Equal(historicalRoot, retainedHistoricalRoot);
        AssertPackageEvidenceEqual(protectedEvidence, CapturePackageEvidence(fixture, protectedPackages));
        AssertPackageEvidenceEqual(historicalGraphEvidence,
            CapturePackageEvidence(fixture, historicalGraphPackages));
        AssertStateEvidenceEqual(stateEvidence, await CaptureStateEvidenceAsync(fixture));

        var repeated = await RunExecutionAsync(fixture, cancellationToken: deadline.Token);
        Assert.Equal("execution-result", repeated.Kind);
        Assert.Equal("Completed", String(repeated.Message, "status"));
        Assert.Equal("AlreadyAbsent",
            String(Candidate(repeated.Message, "Unrelated.Package", "0.1.0"), "physicalOutcome"));
        AssertHistoricalOfflineMemberOutcome(repeated.Message);
        AssertInstallIdentityPresent(fixture, historicalRoot);
        AssertPackageEvidenceEqual(protectedEvidence, CapturePackageEvidence(fixture, protectedPackages));
        AssertPackageEvidenceEqual(historicalGraphEvidence,
            CapturePackageEvidence(fixture, historicalGraphPackages));
        AssertStateEvidenceEqual(stateEvidence, await CaptureStateEvidenceAsync(fixture));
    }

    [Fact]
    public async Task ExecuteEligibleCandidatesAsync_WhenCancelledAfterPayloadRemoval_PreservesPriorDeletedAndResumes()
    {
        using var fixture = await OverlappingPackageGraphProtectionTests.GraphUseFixture.CreateAsync(
            PackageLoadMode.HostIntegrated);
        var later = fixture.CreateAdditionalEligibleInstall("Zzz.Later.Package", "0.1.0");
        var preflight = await InspectAsync(fixture);
        var stateBefore = await CaptureStateEvidenceAsync(fixture);
        var protectedPackages = await PersistedProtectedPackagesAsync(fixture, preflight);
        var protectedBefore = CapturePackageEvidence(fixture, protectedPackages);
        var laterBefore = CapturePackageEvidence(fixture, [later]);
        Assert.Equal(new[] { "Root.Second", "Unrelated.Package", "Zzz.Later.Package" },
            EligibleRows(preflight).Select(static row => row.Install.PackageId));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var cancelled = await StartPausedExecutionAsync(fixture,
            cancelAtDeletionPoint: "AfterPayloadEntryRemoved", cancelAtEligibleOrdinal: 2,
            cancellationToken: deadline.Token);
        Assert.Equal("Unrelated.Package", String(cancelled.Checkpoint, "packageId"));
        using var response = await cancelled.Process.ReadResponseAsync("execution-result", deadline.Token);
        Assert.Equal(0, (await cancelled.Process.WaitForExitAsync(deadline.Token)).ExitCode);
        var report = response.RootElement.Clone();
        Assert.Equal("Cancelled", String(report, "status"));

        var first = Candidate(report, "Root.Second", "2.0.0");
        Assert.Equal("Completed", String(first, "attempt"));
        Assert.Equal("Deleted", String(first, "physicalOutcome"));
        AssertOfflineMemberOutcome(report);

        var second = Candidate(report, "Unrelated.Package", "0.1.0");
        Assert.Equal("Cancelled", String(second, "attempt"));
        Assert.Equal("PartiallyRemoved", String(second, "physicalOutcome"));
        Assert.NotEmpty(second.GetProperty("missingAtLastObservation").EnumerateArray());

        var laterOutcome = Candidate(report, "Zzz.Later.Package", "0.1.0");
        Assert.Equal("NotAttempted", String(laterOutcome, "attempt"));
        Assert.Null(laterOutcome.GetProperty("physicalOutcome").GetString());

        AssertInstallAbsent(fixture, "Root.Second", "2.0.0");
        Assert.Equal("Unrelated.Package", String(cancelled.Checkpoint, "packageId"));
        Assert.Equal("0.1.0", String(cancelled.Checkpoint, "version"));
        var partialCandidateId = Guid.ParseExact(String(cancelled.Checkpoint, "candidateId"), "N");
        var partialJournal = DiscoverJournalByCandidate(fixture, partialCandidateId).Journal;
        AssertCheckpointNamespace(fixture, partialJournal, "AfterPayloadEntryRemoved");
        AssertPackageEvidenceEqual(laterBefore, CapturePackageEvidence(fixture, [later]));
        AssertStateEvidenceEqual(stateBefore, await CaptureStateEvidenceAsync(fixture));
        AssertPackageEvidenceEqual(protectedBefore, CapturePackageEvidence(fixture, protectedPackages));

        var resumed = await RunExecutionAsync(fixture, cancellationToken: deadline.Token);
        Assert.Equal("execution-result", resumed.Kind);
        Assert.Equal("Completed", String(resumed.Message, "status"));
        AssertOfflineMemberOutcome(resumed.Message);
        Assert.Equal("AlreadyAbsent", String(Candidate(resumed.Message, "Root.Second", "2.0.0"), "physicalOutcome"));
        Assert.Equal("Deleted", String(Candidate(resumed.Message, "Unrelated.Package", "0.1.0"), "physicalOutcome"));
        Assert.Equal("Deleted", String(Candidate(resumed.Message, "Zzz.Later.Package", "0.1.0"), "physicalOutcome"));
        AssertStateEvidenceEqual(stateBefore, await CaptureStateEvidenceAsync(fixture));
        AssertPackageEvidenceEqual(protectedBefore, CapturePackageEvidence(fixture, protectedPackages));
        AssertInstallAbsent(fixture, "Unrelated.Package", "0.1.0");
        AssertInstallAbsent(fixture, "Zzz.Later.Package", "0.1.0");
    }

    [Fact]
    public async Task ExecuteEligibleCandidatesAsync_WhenCancelledBeforeAdmission_DoesNotMutate()
    {
        using var fixture = await OverlappingPackageGraphProtectionTests.GraphUseFixture.CreateAsync(
            PackageLoadMode.HostIntegrated);
        var preflight = await InspectAsync(fixture);
        Assert.NotEmpty(EligibleRows(preflight));
        var packages = AllFixturePackages(fixture);
        var stateBefore = await CaptureStateEvidenceAsync(fixture);
        var packageBefore = CapturePackageEvidence(fixture, packages);
        var originalRootSecond = FindPackage(fixture, "Root.Second", "2.0.0");
        var originalUnrelated = fixture.UnrelatedInstall;

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var result = await RunExecutionAsync(fixture, cancelBeforeWork: true,
            cancellationToken: deadline.Token);
        Assert.Equal("cancelled-before-work", result.Kind);
        Assert.Equal(0, result.Exit.ExitCode);
        AssertNoDeletionJournals(fixture);
        AssertPackageEvidenceEqual(packageBefore, CapturePackageEvidence(fixture, packages));
        AssertStateEvidenceEqual(stateBefore, await CaptureStateEvidenceAsync(fixture));
        AssertInstallIdentityPresent(fixture, GetInstallIdentity(fixture, originalRootSecond));
        AssertInstallIdentityPresent(fixture, GetInstallIdentity(fixture, originalUnrelated));
    }

    [Fact]
    public async Task ExecuteEligibleCandidatesAsync_WhenPreviewCandidateBecomesProtectedByFreshMemberState_ReplansAndRetainsIt()
    {
        using var fixture = await OverlappingPackageGraphProtectionTests.GraphUseFixture.CreateAsync(
            PackageLoadMode.HostIntegrated);
        var stateBefore = await CaptureStateEvidenceAsync(fixture);
        var preview = await InspectAsync(fixture);
        var newlyProtected = preview.RetentionPlan!.Entries.Single(entry =>
            entry.Install.PackageId == "Root.Second" && entry.Install.Version == "2.0.0");
        Assert.Equal(PackageStoreRetentionClassification.Eligible, newlyProtected.Classification);

        await PublishMemberBReplacementAsync(fixture);
        var freshPreview = await InspectAsync(fixture);
        var packages = await PersistedProtectedPackagesAsync(fixture, freshPreview);
        var packagesBefore = CapturePackageEvidence(fixture, packages);
        var protectedV2 = freshPreview.RetentionPlan!.Entries.Single(entry =>
            entry.Install.PackageId == "Root.Second" && entry.Install.Version == "2.0.0");
        Assert.Equal(PackageStoreRetentionClassification.Retained, protectedV2.Classification);
        Assert.Contains(PackageStoreRetentionReason.ProtectedActive, protectedV2.Reasons);
        await AssertOfflineMemberProtectionAsync(fixture, freshPreview);
        Assert.Equal(new[] { ("Root.Second", "1.0.0"), ("Unrelated.Package", "0.1.0") },
            EligibleRows(freshPreview).Select(static row => (row.Install.PackageId, row.Install.Version)));

        var stateAfterCoordinatedUpdate = await CaptureStateEvidenceAsync(fixture);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var result = await RunExecutionAsync(fixture, cancellationToken: deadline.Token);
        Assert.Equal("execution-result", result.Kind);
        Assert.Equal("Completed", String(result.Message, "status"));
        var activeOutcome = Candidate(result.Message, "Root.Second", "2.0.0");
        Assert.Equal("Retained", String(activeOutcome, "physicalOutcome"));
        Assert.Equal("NotAttempted", String(activeOutcome, "attempt"));
        Assert.Equal("Deleted", String(Candidate(result.Message, "Root.Second", "1.0.0"), "physicalOutcome"));
        Assert.Equal("Deleted", String(Candidate(result.Message, "Unrelated.Package", "0.1.0"), "physicalOutcome"));
        AssertOfflineMemberOutcome(result.Message, "2.0.0");

        AssertStateEvidenceEqual(stateAfterCoordinatedUpdate, await CaptureStateEvidenceAsync(fixture));
        AssertPackageEvidenceEqual(packagesBefore, CapturePackageEvidence(fixture, packages));
        AssertInstallPresent(fixture, "Root.Second", "2.0.0");
        AssertInstallAbsent(fixture, "Root.Second", "1.0.0");
        AssertInstallAbsent(fixture, "Unrelated.Package", "0.1.0");
        Assert.NotEqual(stateBefore["member-b"].Digest, stateAfterCoordinatedUpdate["member-b"].Digest);
    }

    [Fact]
    public async Task ExecuteEligibleCandidatesAsync_WhenMemberStateBodyChangesAfterPreview_RefusesWithoutMutation()
    {
        using var fixture = await OverlappingPackageGraphProtectionTests.GraphUseFixture.CreateAsync(
            PackageLoadMode.HostIntegrated);
        await PublishMemberBReplacementAsync(fixture);
        var preflight = await InspectAsync(fixture);
        await PersistedProtectedPackagesAsync(fixture, preflight);
        var packages = AllFixturePackages(fixture);
        var packagesBefore = CapturePackageEvidence(fixture, packages);
        var state = await new StoreStateSerializer().LoadAsync(fixture.StatePaths["member-a"], CancellationToken.None);
        var mismatched = state with { UpdatedAt = state.UpdatedAt.AddSeconds(1) };
        await WriteStateBodyMismatchAsync(fixture.StatePaths["member-a"], mismatched);
        var tamperedState = await CaptureStateEvidenceAsync(fixture);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var refusal = await RunExecutionAsync(fixture, cancellationToken: deadline.Token);
        Assert.Equal("refused", refusal.Kind);
        Assert.Contains(String(refusal.Message, "reason"), new[] { "StateMismatch", "UnknownAuthority" });
        AssertNoDeletionJournals(fixture);
        AssertStateEvidenceEqual(tamperedState, await CaptureStateEvidenceAsync(fixture));
        AssertPackageEvidenceEqual(packagesBefore, CapturePackageEvidence(fixture, packages));
        AssertInstallPresent(fixture, "Unrelated.Package", "0.1.0");
    }

    [Fact]
    public async Task ExecuteEligibleCandidatesAsync_WhenUnexpectedQuarantineEntryAppearsAfterInterruption_RefusesWithoutRemoval()
    {
        using var fixture = await OverlappingPackageGraphProtectionTests.GraphUseFixture.CreateAsync(
            PackageLoadMode.HostIntegrated);
        var preflight = await InspectAsync(fixture);
        var packages = await PersistedProtectedPackagesAsync(fixture, preflight);
        var protectedBefore = CapturePackageEvidence(fixture, packages);
        var stateBefore = await CaptureStateEvidenceAsync(fixture);
        var unrelatedIdentity = GetInstallIdentity(fixture, fixture.UnrelatedInstall);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var interrupted = await StartPausedExecutionAsync(fixture,
            stopAtDeletionPoint: "AfterDeletingPublished", cancellationToken: deadline.Token);
        Assert.Equal("Root.Second", String(interrupted.Checkpoint, "packageId"));
        Assert.NotEqual(0, (await interrupted.Process.TerminateAsync(deadline.Token)).ExitCode);
        var journal = DiscoverSingleJournal(fixture);
        AssertCheckpointNamespace(fixture, journal.Journal, "AfterDeletingPublished");

        var sentinelName = $"unexpected-{Guid.NewGuid():N}.bin";
        var sentinelContents = "must remain byte-for-byte"u8.ToArray();
        var sentinelIdentity = CreateQuarantineSentinel(fixture, journal.Journal, sentinelName, sentinelContents);
        var journalPayloadBeforeAdmission = journal.Payload.ToArray();
        var namespaceBeforeAdmission = CaptureCandidateNamespace(fixture, journal.Journal);

        await AssertOrdinaryAdmissionDoesNotResumeAsync(fixture, deadline.Token);
        Assert.Equal(journalPayloadBeforeAdmission, DiscoverSingleJournal(fixture).Payload);
        AssertCandidateNamespaceEqual(namespaceBeforeAdmission,
            CaptureCandidateNamespace(fixture, journal.Journal));

        var refusal = await RunExecutionAsync(fixture, cancellationToken: deadline.Token);
        Assert.Equal("refused", refusal.Kind);
        Assert.Equal("UnknownAuthority", String(refusal.Message, "reason"));
        var journalAfterRefusal = DiscoverSingleJournal(fixture);
        Assert.Equal(journalPayloadBeforeAdmission, journalAfterRefusal.Payload);
        Assert.Equal(journal.FileIdentity, journalAfterRefusal.FileIdentity);
        AssertCandidateNamespaceEqual(namespaceBeforeAdmission,
            CaptureCandidateNamespace(fixture, journalAfterRefusal.Journal));
        Assert.Equal(sentinelIdentity, ReadQuarantineSentinelIdentity(fixture, journal.Journal, sentinelName));
        Assert.Equal(sentinelContents, ReadQuarantineSentinel(fixture, journal.Journal, sentinelName));
        Assert.Equal(unrelatedIdentity, GetInstallIdentity(fixture, fixture.UnrelatedInstall));
        AssertInstallPresent(fixture, "Unrelated.Package", "0.1.0");
        AssertStateEvidenceEqual(stateBefore, await CaptureStateEvidenceAsync(fixture));
        AssertPackageEvidenceEqual(protectedBefore, CapturePackageEvidence(fixture, packages));
    }

    private static async Task<PackageStoreInspectionSnapshot> InspectAsync(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture)
    {
        using var provider = fixture.CreateProvider("member-a", participant: true);
        var admission = Assert.IsType<PackageStoreAdmission>(provider.GetRequiredService<IPackageStoreAdmission>());
        var files = admission.Registry.Files;
        var inspection = new PackageStoreInspectionService(files, admission.Registry,
            provider.GetRequiredService<ITrustedPackageStoreRootCatalog>(),
            provider.GetRequiredService<IPackageStoreRootResolver>(),
            new PackageStoreInventory(files), new PackageStoreRetentionPlanner());
        return await inspection.InspectRootAsync("default", 0, CancellationToken.None);
    }

    private static async Task PublishMemberBReplacementAsync(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture)
    {
        var serializer = new StoreStateSerializer();
        var path = fixture.StatePaths["member-b"];
        var current = await serializer.LoadAsync(path, CancellationToken.None);
        var candidate = fixture.BuildProtectedState("member-b", fixture.Graphs["member-b-v2"], current,
            revision: 2, isUpdate: true);
        await fixture.PublishProtectedStateAsync("member-b", candidate);
        var reread = await serializer.LoadAsync(path, CancellationToken.None);
        Assert.Equal("2.0.0", reread.ActiveVersionById["Root.Second"]);
        Assert.Equal("2.0.0", reread.LastKnownGoodById["Root.Second"]);
        Assert.Equal(2, reread.ProtectionRecord!.Revision);
        Assert.Equal(ProtectionDigest.StateBody(reread), reread.ProtectionRecord.StateBodyDigest);
        Assert.Equal(ProtectionDigest.Protection(reread.ProtectionRecord), reread.ProtectionRecord.ProtectionDigest);
    }

    private static StoreStateRecord CreateHistoricalLkgOnlyMemberState(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture,
        StoreStateRecord currentState)
    {
        var currentProtection = Assert.IsType<PackageProtectionRecord>(currentState.ProtectionRecord);
        var activeState = fixture.BuildProtectedState("member-b", fixture.Graphs["member-b-v2"], currentState,
            revision: currentProtection.Revision + 1, isUpdate: true);
        var activeProtection = Assert.IsType<PackageProtectionRecord>(activeState.ProtectionRecord);
        var stateBody = activeState with
        {
            LastKnownGoodById = new Dictionary<string, string>(currentState.LastKnownGoodById,
                StringComparer.OrdinalIgnoreCase),
            ProtectionRecord = null
        };
        Assert.Equal("2.0.0", stateBody.ActiveVersionById["Root.Second"]);
        Assert.Equal("1.0.0", stateBody.LastKnownGoodById["Root.Second"]);

        PackageProtectionRecord CreateProtectionRecord(string protectionDigest)
            => new(activeProtection.SchemaVersion, activeProtection.RootIdentity, activeProtection.EnrollmentEpoch,
                activeProtection.MemberId, activeProtection.Revision, ProtectionDigest.StateBody(stateBody),
                protectionDigest, activeProtection.ActiveClosure, currentProtection.RecoverableClosure,
                activeProtection.RetiredGraphs, activeProtection.LegacyUnknownRecovery);

        var digestInput = CreateProtectionRecord(new string('0', 64));
        var protection = CreateProtectionRecord(ProtectionDigest.Protection(digestInput));
        return stateBody with { ProtectionRecord = protection };
    }

    private static async Task AssertOfflineMemberProtectionAsync(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture,
        PackageStoreInspectionSnapshot inspection)
    {
        Assert.Equal(PackageStoreRetentionProtectionKnowledge.Known, inspection.ProtectionKnowledge);
        Assert.Empty(inspection.Uses);
        var plan = inspection.RetentionPlan!;
        var serializer = new StoreStateSerializer();
        var memberA = (await serializer.LoadAsync(fixture.StatePaths["member-a"], CancellationToken.None)).ProtectionRecord!;
        var memberB = (await serializer.LoadAsync(fixture.StatePaths["member-b"], CancellationToken.None)).ProtectionRecord!;
        // The persisted contract currently mirrors a member's active snapshots into its recoverable
        // closure. This proves an offline member's protection, not historical-LKG-only protection.
        var memberBActiveGraph = Assert.Single(memberB.ActiveClosure.Graphs!);
        var memberBRecoverableGraph = Assert.Single(memberB.RecoverableClosure!.Graphs!);
        Assert.Equal(memberBActiveGraph.GenerationId, memberBRecoverableGraph.GenerationId);
        Assert.DoesNotContain(memberA.ActiveClosure.Graphs!, graph =>
            graph.GenerationId == memberBActiveGraph.GenerationId);
        Assert.DoesNotContain(inspection.Uses, use =>
            use.Graph.GenerationId == memberBActiveGraph.GenerationId);
        var memberBRootVersion = memberB.ActiveClosure.Graphs!.SelectMany(static graph => graph.Nodes)
            .Select(static node => node.Install)
            .Single(static install => install.PackageId == "Root.Second");
        var memberBRecoverableRootVersion = memberB.RecoverableClosure!.Graphs!.SelectMany(static graph => graph.Nodes)
            .Select(static node => node.Install)
            .Single(static install => install.PackageId == "Root.Second");
        Assert.Equal(memberBRootVersion, memberBRecoverableRootVersion);
        Assert.Equal("Root.Second", memberBRootVersion.PackageId);
        Assert.DoesNotContain(memberA.ActiveClosure.Graphs!.SelectMany(static graph => graph.Nodes), static node =>
            node.Install.PackageId == "Root.Second");
        Assert.DoesNotContain(memberA.RecoverableClosure!.Graphs!.SelectMany(static graph => graph.Nodes), static node =>
            node.Install.PackageId == "Root.Second");
        Assert.DoesNotContain(inspection.Uses.SelectMany(static use => use.Graph.Nodes), static node =>
            node.Install.PackageId == "Root.Second" && node.Install.Version == "1.0.0");

        var offline = plan.Entries.Single(entry => entry.Install == memberBRootVersion);
        Assert.Equal(PackageStoreRetentionClassification.Retained, offline.Classification);
        Assert.Contains(PackageStoreRetentionReason.ProtectedActive, offline.Reasons);
        Assert.Contains(PackageStoreRetentionReason.ProtectedRecoverableLastKnownGood, offline.Reasons);
        Assert.DoesNotContain(PackageStoreRetentionReason.ProtectedLiveUse, offline.Reasons);
    }

    private static async Task<PackageInstallIdentity> AssertOfflineHistoricalLkgOnlyProtectionAsync(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture,
        PackageStoreInspectionSnapshot inspection)
    {
        Assert.Equal(PackageStoreRetentionProtectionKnowledge.Known, inspection.ProtectionKnowledge);
        Assert.Empty(inspection.Uses);
        var state = await new StoreStateSerializer().LoadAsync(fixture.StatePaths["member-b"], CancellationToken.None);
        var protection = Assert.IsType<PackageProtectionRecord>(state.ProtectionRecord);
        Assert.Equal("2.0.0", state.ActiveVersionById["Root.Second"]);
        Assert.Equal("1.0.0", state.LastKnownGoodById["Root.Second"]);

        var activeGraph = Assert.Single(protection.ActiveClosure.Graphs!);
        var historicalGraph = Assert.Single(protection.RecoverableClosure.Graphs!);
        Assert.Equal(fixture.Graphs["member-b-v2"].Graph.GenerationId, activeGraph.GenerationId);
        Assert.Equal(fixture.Graphs["member-b"].Graph.GenerationId, historicalGraph.GenerationId);
        Assert.NotEqual(activeGraph.GenerationId, historicalGraph.GenerationId);

        var historicalRoot = Assert.Single(historicalGraph.Nodes,
            static node => node.Install.PackageId == "Root.Second").Install;
        Assert.Equal("1.0.0", historicalRoot.Version);
        Assert.DoesNotContain(activeGraph.Nodes, node => node.Install == historicalRoot);
        var entry = Assert.Single(inspection.RetentionPlan!.Entries,
            candidate => candidate.Install == historicalRoot);
        Assert.Equal(PackageStoreRetentionClassification.Retained, entry.Classification);
        Assert.Contains(PackageStoreRetentionReason.ProtectedRecoverableLastKnownGood, entry.Reasons);
        Assert.DoesNotContain(PackageStoreRetentionReason.ProtectedActive, entry.Reasons);
        Assert.DoesNotContain(PackageStoreRetentionReason.ProtectedLiveUse, entry.Reasons);
        return historicalRoot;
    }

    private static void AssertOfflineMemberOutcome(JsonElement result)
        => AssertOfflineMemberOutcome(result, "1.0.0");

    private static void AssertOfflineMemberOutcome(JsonElement result, string version)
    {
        var offlineMember = Candidate(result, "Root.Second", version);
        Assert.Equal("Retained", String(offlineMember, "classification"));
        Assert.Equal("Retained", String(offlineMember, "physicalOutcome"));
        Assert.Contains("ProtectedRecoverableLastKnownGood",
            offlineMember.GetProperty("reasons").EnumerateArray().Select(static reason => reason.GetString()));
        Assert.Contains("ProtectedActive",
            offlineMember.GetProperty("reasons").EnumerateArray().Select(static reason => reason.GetString()));
        Assert.DoesNotContain("ProtectedLiveUse",
            offlineMember.GetProperty("reasons").EnumerateArray().Select(static reason => reason.GetString()));
    }

    private static void AssertHistoricalOfflineMemberOutcome(JsonElement result)
    {
        var offlineMember = Candidate(result, "Root.Second", "1.0.0");
        Assert.Equal("Retained", String(offlineMember, "classification"));
        Assert.Equal("Retained", String(offlineMember, "physicalOutcome"));
        var reasons = offlineMember.GetProperty("reasons").EnumerateArray()
            .Select(static reason => reason.GetString()).ToArray();
        Assert.Contains("ProtectedRecoverableLastKnownGood", reasons);
        Assert.DoesNotContain("ProtectedActive", reasons);
        Assert.DoesNotContain("ProtectedLiveUse", reasons);
    }

    private static async Task<PausedExecution> StartPausedExecutionAsync(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture,
        string? stopAtDeletionPoint = null,
        string? stopAtPublicationPoint = null,
        string? cancelAtDeletionPoint = null,
        int cancelAtEligibleOrdinal = 0,
        CancellationToken cancellationToken = default)
    {
        var request = await WriteExecutionRequestAsync(fixture, stopAtDeletionPoint,
            stopAtPublicationPoint, cancelAtDeletionPoint, cancelAtEligibleOrdinal,
            cancelBeforeWork: false, cancellationToken);
        PackageStoreParticipantProcess process;
        try
        {
            process = await PackageStoreParticipantProcess.StartExecutionAsync(
                request.OperationId, request.Path, cancellationToken);
        }
        finally
        {
            File.Delete(request.Path);
        }

        try
        {
            using var checkpoint = await process.ReadResponseAsync("checkpoint", cancellationToken);
            return new PausedExecution(process, checkpoint.RootElement.Clone());
        }
        catch
        {
            await process.DisposeAsync();
            throw;
        }
    }

    private static async Task<ExecutionResponse> RunExecutionAsync(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture,
        bool cancelBeforeWork = false,
        CancellationToken cancellationToken = default)
    {
        var request = await WriteExecutionRequestAsync(fixture, stopAtDeletionPoint: null,
            stopAtPublicationPoint: null, cancelAtDeletionPoint: null, cancelAtEligibleOrdinal: 0,
            cancelBeforeWork, cancellationToken);
        try
        {
            await using var process = await PackageStoreParticipantProcess.StartExecutionAsync(
                request.OperationId, request.Path, cancellationToken);
            using var response = await process.ReadNextResponseAsync(cancellationToken);
            var message = response.RootElement.Clone();
            var exit = await process.WaitForExitAsync(cancellationToken);
            Assert.Equal(0, exit.ExitCode);
            return new ExecutionResponse(String(message, "kind"), message, exit);
        }
        finally
        {
            File.Delete(request.Path);
        }
    }

    private static async Task<ExecutionRequestFile> WriteExecutionRequestAsync(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture,
        string? stopAtDeletionPoint,
        string? stopAtPublicationPoint,
        string? cancelAtDeletionPoint,
        int cancelAtEligibleOrdinal,
        bool cancelBeforeWork,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operationId = Guid.NewGuid().ToString("N");
        var requestPath = Path.Combine(Path.GetTempPath(), $"nuplane-execution-{operationId}.json");
        var request = new
        {
            OperationId = operationId,
            RootPath = fixture.RootPath,
            StateFilePath = fixture.StatePaths["member-a"],
            CompleteRoster = fixture.CompleteRoster,
            EnrollmentEpoch,
            KeepNewestInactiveVersionsPerPackage = 0,
            RootLabel = "default",
            StopAtJournalPublicationPoint = stopAtPublicationPoint,
            StopAtDeletionCheckpoint = stopAtDeletionPoint,
            CancelAtDeletionCheckpoint = cancelAtDeletionPoint,
            CancelAtEligibleCandidateOrdinal = cancelAtEligibleOrdinal,
            CancelBeforeWork = cancelBeforeWork
        };
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, RequestJsonOptions), cancellationToken);
        return new ExecutionRequestFile(operationId, requestPath);
    }

    private static async Task AssertOrdinaryAdmissionDoesNotResumeAsync(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture,
        CancellationToken cancellationToken)
    {
        using var provider = fixture.CreateProvider("member-a", participant: true);
        var admission = provider.GetRequiredService<IPackageStoreAdmission>();
        try
        {
            await using var ordinary = await admission.AcquireConfiguredRootOperationAsync(
                PackageStoreAdmissionKind.Loading, cancellationToken);
            Assert.Equal(PackageStoreAdmissionStatus.Enrolled, ordinary.Status);
            Assert.Equal(fixture.RootIdentity, ordinary.Root);
        }
        catch (PackageStoreAdmissionException)
        {
            // Ordinary admission may conservatively refuse a root with an interrupted journal.
        }
    }

    private static PackageStoreDeletionJournalStore.DiscoveredJournal DiscoverSingleJournal(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture)
        => Assert.Single(DiscoverJournals(fixture));

    private static PackageStoreDeletionJournalStore.DiscoveredJournal DiscoverJournalByCandidate(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture,
        Guid candidateId)
        => Assert.Single(DiscoverJournals(fixture),
            journal => journal.Journal.CandidateId == candidateId);

    private static IReadOnlyList<PackageStoreDeletionJournalStore.DiscoveredJournal> DiscoverJournals(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture)
    {
        return new PackageStoreDeletionJournalStore(fixture.Files)
            .Discover(fixture.Root, fixture.RootIdentity, EnrollmentEpoch, CancellationToken.None);
    }

    private static void AssertNoDeletionJournals(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture)
        => Assert.Empty(new PackageStoreDeletionJournalStore(fixture.Files)
            .Discover(fixture.Root, fixture.RootIdentity, EnrollmentEpoch, CancellationToken.None));

    private static void AssertCheckpointNamespace(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture,
        PackageStoreDeletionJournal journal,
        string point)
    {
        var files = fixture.Files;
        using var parent = OpenDirectoryPath(files, fixture.Root, journal.PackageParentRelativeComponents);
        Assert.Equal(journal.PackageParentIdentity, files.InspectHandle(parent).Identity);
        Assert.Equal(journal.PackageParentSemantics,
            ((IPhysicalStoreNameFileSystem)files).ObserveDirectoryNameSemantics(parent));
        var original = files.InspectChildNoFollow(parent, journal.OriginalBasename);
        var quarantine = files.InspectChildNoFollow(parent, journal.QuarantineBasename);

        if (point == "Prepared")
        {
            Assert.Equal(PackageStoreDeletionPhase.Prepared, journal.Phase);
            Assert.Equal(journal.Manifest.InstallDirectoryIdentity, original?.Identity);
            Assert.Null(quarantine);
            using var originalDirectory = files.OpenDirectoryChildNoFollow(parent, journal.OriginalBasename);
            AssertManifestNames(fixture, originalDirectory, journal.Manifest.Entries);
            return;
        }

        Assert.Null(original);
        if (point == "AfterQuarantineRootRemoved")
        {
            Assert.Equal(PackageStoreDeletionPhase.Finalizing, journal.Phase);
            Assert.Null(quarantine);
            return;
        }

        Assert.Equal(PhysicalStoreEntryKind.Directory, quarantine?.Kind);
        Assert.Equal(journal.Manifest.InstallDirectoryIdentity, quarantine?.Identity);
        using var quarantineDirectory = files.OpenDirectoryChildNoFollow(parent, journal.QuarantineBasename);
        var names = ReadDirectoryEntries(files, quarantineDirectory);
        var expectedNames = point switch
        {
            "AfterQuarantinedPublished" or "AfterDeletingPublished" =>
                journal.Manifest.Entries.Select(static entry => entry.RelativeComponents.Single()).ToArray(),
            "AfterPayloadEntryRemoved" => names.Keys.ToArray(),
            "AfterFinalizingPublished" => [PackageStoreDeletionJournal.CompletionMarkerName],
            "AfterMarkerRemoved" or "AfterQuarantineHandleClosed" => [],
            _ => throw new Xunit.Sdk.XunitException($"Unknown checkpoint '{point}'.")
        };
        if (point == "AfterPayloadEntryRemoved")
        {
            Assert.Equal(journal.Manifest.Entries.Count() - 1, names.Count);
            Assert.Contains(PackageStoreDeletionJournal.CompletionMarkerName, names.Keys);
            Assert.NotEqual(journal.Manifest.Entries.Count(), names.Count);
        }
        else
        {
            Assert.Equal(expectedNames.Order(StringComparer.Ordinal), names.Keys.Order(StringComparer.Ordinal));
        }

        foreach (var (name, identity) in names)
        {
            var manifestEntry = journal.Manifest.Entries.Single(entry =>
                entry.RelativeComponents.SequenceEqual([name], StringComparer.Ordinal));
            Assert.Equal(manifestEntry.Identity, identity);
        }
    }

    private static void AssertManifestNames(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture,
        PhysicalStoreDirectoryHandle directory,
        IReadOnlyList<PhysicalStoreDeletionManifestEntry> manifest)
    {
        var entries = ReadDirectoryEntries(fixture.Files, directory);
        Assert.Equal(manifest.Select(static entry => entry.RelativeComponents.Single()).Order(StringComparer.Ordinal),
            entries.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, identity) in entries)
            Assert.Equal(manifest.Single(entry => entry.RelativeComponents.SequenceEqual([name], StringComparer.Ordinal)).Identity,
                identity);
    }

    private static IReadOnlyDictionary<string, PhysicalFileIdentity> ReadDirectoryEntries(
        IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle directory)
    {
        var enumeration = Assert.IsAssignableFrom<IPhysicalStoreDirectoryEnumerationFileSystem>(files);
        return enumeration.EnumerateChildNamesNoFollow(directory, 128).ToDictionary(name => name,
            name => Assert.IsType<PhysicalStoreEntryInfo>(files.InspectChildNoFollow(directory, name)).Identity,
            StringComparer.Ordinal);
    }

    private static CandidateNamespace CaptureCandidateNamespace(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture,
        PackageStoreDeletionJournal journal)
    {
        using var parent = OpenDirectoryPath(fixture.Files, fixture.Root, journal.PackageParentRelativeComponents);
        var original = fixture.Files.InspectChildNoFollow(parent, journal.OriginalBasename);
        var quarantine = fixture.Files.InspectChildNoFollow(parent, journal.QuarantineBasename);
        var names = new Dictionary<string, PhysicalFileIdentity>(StringComparer.Ordinal);
        var payloadDigests = new Dictionary<string, string>(StringComparer.Ordinal);
        if (quarantine?.Kind == PhysicalStoreEntryKind.Directory)
        {
            using var quarantineDirectory = fixture.Files.OpenDirectoryChildNoFollow(parent, journal.QuarantineBasename);
            foreach (var pair in ReadDirectoryEntries(fixture.Files, quarantineDirectory))
            {
                names.Add(pair.Key, pair.Value);
                using var file = fixture.Files.OpenFileChildNoFollow(quarantineDirectory, pair.Key, FileAccess.Read);
                Assert.Equal(pair.Value, fixture.Files.InspectHandle(file).Identity);
                payloadDigests.Add(pair.Key, Convert.ToHexString(SHA256.HashData(
                    fixture.Files.ReadControlFile(file, maximumBytes: 16 * 1024 * 1024))));
            }
        }

        return new CandidateNamespace(fixture.Files.InspectHandle(parent).Identity,
            ((IPhysicalStoreNameFileSystem)fixture.Files).ObserveDirectoryNameSemantics(parent),
            original?.Identity, quarantine?.Identity, names, payloadDigests);
    }

    private static void AssertCandidateNamespaceEqual(CandidateNamespace expected, CandidateNamespace actual)
    {
        Assert.Equal(expected.ParentIdentity, actual.ParentIdentity);
        Assert.Equal(expected.ParentSemantics, actual.ParentSemantics);
        Assert.Equal(expected.OriginalIdentity, actual.OriginalIdentity);
        Assert.Equal(expected.QuarantineIdentity, actual.QuarantineIdentity);
        Assert.Equal(expected.Entries.Keys.Order(StringComparer.Ordinal), actual.Entries.Keys.Order(StringComparer.Ordinal));
        foreach (var name in expected.Entries.Keys)
        {
            Assert.Equal(expected.Entries[name], actual.Entries[name]);
            Assert.Equal(expected.PayloadDigests[name], actual.PayloadDigests[name]);
        }
    }

    private static PhysicalFileIdentity CreateQuarantineSentinel(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture,
        PackageStoreDeletionJournal journal,
        string name,
        byte[] contents)
    {
        using var parent = OpenDirectoryPath(fixture.Files, fixture.Root, journal.PackageParentRelativeComponents);
        using var quarantine = fixture.Files.OpenDirectoryChildNoFollow(parent, journal.QuarantineBasename);
        using var file = fixture.Files.CreateFileExclusiveAt(quarantine, name);
        fixture.Files.WriteNewControlFile(file, contents);
        return fixture.Files.InspectHandle(file).Identity;
    }

    private static PhysicalFileIdentity ReadQuarantineSentinelIdentity(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture,
        PackageStoreDeletionJournal journal,
        string name)
    {
        using var parent = OpenDirectoryPath(fixture.Files, fixture.Root, journal.PackageParentRelativeComponents);
        using var quarantine = fixture.Files.OpenDirectoryChildNoFollow(parent, journal.QuarantineBasename);
        return Assert.IsType<PhysicalStoreEntryInfo>(fixture.Files.InspectChildNoFollow(quarantine, name)).Identity;
    }

    private static byte[] ReadQuarantineSentinel(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture,
        PackageStoreDeletionJournal journal,
        string name)
    {
        using var parent = OpenDirectoryPath(fixture.Files, fixture.Root, journal.PackageParentRelativeComponents);
        using var quarantine = fixture.Files.OpenDirectoryChildNoFollow(parent, journal.QuarantineBasename);
        using var file = fixture.Files.OpenFileChildNoFollow(quarantine, name, FileAccess.Read);
        return fixture.Files.ReadControlFile(file, maximumBytes: 512);
    }

    private static PhysicalStoreDirectoryHandle OpenDirectoryPath(
        IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle root,
        IEnumerable<string> components)
    {
        PhysicalStoreDirectoryHandle? current = null;
        var parent = root;
        try
        {
            foreach (var component in components)
            {
                var next = files.OpenDirectoryChildNoFollow(parent, component);
                current?.Dispose();
                current = next;
                parent = current;
            }

            return current ?? throw new ArgumentException("A package parent must have at least one component.", nameof(components));
        }
        catch
        {
            current?.Dispose();
            throw;
        }
    }

    private static async Task<IReadOnlyDictionary<string, StateEvidence>> CaptureStateEvidenceAsync(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture)
    {
        var result = new Dictionary<string, StateEvidence>(StringComparer.Ordinal);
        foreach (var (memberId, path) in fixture.StatePaths.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            using var parent = OwnedProcessDirectory.Open(fixture.Files, Path.GetDirectoryName(path)!);
            using var file = fixture.Files.OpenFileChildNoFollow(parent, Path.GetFileName(path), FileAccess.Read);
            result.Add(memberId, new StateEvidence(fixture.Files.InspectHandle(file).Identity,
                await File.ReadAllBytesAsync(path)));
        }

        return result;
    }

    private static void AssertStateEvidenceEqual(
        IReadOnlyDictionary<string, StateEvidence> expected,
        IReadOnlyDictionary<string, StateEvidence> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var member in expected.Keys)
        {
            Assert.Equal(expected[member].Identity, actual[member].Identity);
            Assert.Equal(expected[member].Bytes, actual[member].Bytes);
        }
    }

    private static Dictionary<string, InstallEvidence> CapturePackageEvidence(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture,
        IEnumerable<ResolvedPackage> packages)
    {
        var reader = new PackageInstallIdentityReader(fixture.Files);
        var result = new Dictionary<string, InstallEvidence>(StringComparer.Ordinal);
        foreach (var package in packages.DistinctBy(static package => package.InstallPath, StringComparer.Ordinal))
        {
            var relative = RootRelativePath(fixture, package.InstallPath);
            using var observation = reader.Observe(fixture.Root, fixture.RootIdentity, relative,
                package.Id, package.Version);
            var contents = new[]
            {
                package.Id + ".nuspec",
                package.Id + ".dll",
                Nuplane.Metadata.NuplanePackageMetadataReader.MetadataFileName,
                PackageInstallStore.CompletionMarkerFileName
            };
            var digest = string.Join("\n", contents.Select(name => name + ":" +
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(package.InstallPath, name))))));
            result.Add(relative, new InstallEvidence(observation.InstallIdentity, digest));
        }

        return result;
    }

    private static void AssertPackageEvidenceEqual(
        IReadOnlyDictionary<string, InstallEvidence> expected,
        IReadOnlyDictionary<string, InstallEvidence> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var path in expected.Keys)
        {
            Assert.Equal(expected[path].Identity, actual[path].Identity);
            Assert.Equal(expected[path].PayloadDigest, actual[path].PayloadDigest);
        }
    }

    private static async Task<IReadOnlyList<ResolvedPackage>> PersistedProtectedPackagesAsync(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture,
        PackageStoreInspectionSnapshot snapshot)
    {
        var packagesByPath = AllFixturePackages(fixture).ToDictionary(
            package => RootRelativePath(fixture, package.InstallPath), StringComparer.Ordinal);
        var expected = new Dictionary<string, ExpectedProtection>(StringComparer.Ordinal);
        var serializer = new StoreStateSerializer();
        foreach (var statePath in fixture.StatePaths.Values)
        {
            var state = await serializer.LoadAsync(statePath, CancellationToken.None);
            var protection = Assert.IsType<PackageProtectionRecord>(state.ProtectionRecord);
            AddClosure(protection.ActiveClosure, PackageStoreRetentionReason.ProtectedActive);
            AddClosure(protection.RecoverableClosure,
                PackageStoreRetentionReason.ProtectedRecoverableLastKnownGood);
        }

        Assert.NotEmpty(expected);
        foreach (var protectedInstall in expected.Values)
        {
            var entry = Assert.Single(snapshot.RetentionPlan!.Entries,
                entry => entry.Install == protectedInstall.Install);
            Assert.Equal(PackageStoreRetentionClassification.Retained, entry.Classification);
            foreach (var reason in protectedInstall.Reasons)
                Assert.Contains(reason, entry.Reasons);
        }

        return expected.Keys.Select(path => packagesByPath[path]).ToArray();

        void AddClosure(PackageProtectionClosure closure, PackageStoreRetentionReason reason)
        {
            Assert.Equal(PackageProtectionClosureKnowledge.Known, closure.Knowledge);
            foreach (var install in Assert.IsAssignableFrom<IReadOnlyList<ProtectedGraphSnapshot>>(closure.Graphs)
                         .SelectMany(static graph => graph.Nodes)
                         .Select(static node => node.Install)
                         .Where(install => install.Root == fixture.RootIdentity))
            {
                var relativePath = install.RootRelativeInstallPath;
                if (expected.TryGetValue(relativePath, out var prior))
                {
                    Assert.Equal(prior.Install, install);
                    prior.Reasons.Add(reason);
                }
                else
                {
                    expected.Add(relativePath, new ExpectedProtection(install, [reason]));
                }
            }
        }
    }

    private static IReadOnlyList<ResolvedPackage> AllFixturePackages(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture)
        => fixture.Graphs.Values.SelectMany(static graph => graph.Packages)
            .Append(fixture.UnrelatedInstall)
            .DistinctBy(static package => package.InstallPath, StringComparer.Ordinal)
            .ToArray();

    private static IEnumerable<PackageStoreRetentionPlanEntry> EligibleRows(
        PackageStoreInspectionSnapshot snapshot)
        => snapshot.RetentionPlan!.Entries.Where(static entry =>
            entry.Classification == PackageStoreRetentionClassification.Eligible);

    private static ResolvedPackage FindPackage(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture,
        string packageId,
        string version)
        => AllFixturePackages(fixture)
            .Single(package => package.Id == packageId && package.Version == version);

    private static PackageInstallIdentity GetInstallIdentity(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture,
        ResolvedPackage package)
    {
        using var observation = new PackageInstallIdentityReader(fixture.Files).Observe(fixture.Root,
            fixture.RootIdentity, RootRelativePath(fixture, package.InstallPath), package.Id, package.Version);
        return observation.InstallIdentity;
    }

    private static void AssertInstallIdentityPresent(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture,
        PackageInstallIdentity expected)
    {
        var relative = expected.RootRelativeInstallPath;
        using var observation = new PackageInstallIdentityReader(fixture.Files).Observe(fixture.Root,
            fixture.RootIdentity, relative, expected.PackageId, expected.Version);
        Assert.Equal(expected, observation.InstallIdentity);
    }

    private static void AssertInstallPresent(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture,
        string packageId,
        string version)
        => AssertInstallIdentityPresent(fixture, GetInstallIdentity(fixture,
            FindPackage(fixture, packageId, version)));

    private static void AssertInstallAbsent(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture,
        string packageId,
        string version)
    {
        var relativePath = $"packages/feed/{packageId}/{version}";
        PhysicalStoreDirectoryHandle? current = null;
        var parent = fixture.Root;
        try
        {
            foreach (var component in new[] { "packages", "feed", packageId })
            {
                var entry = fixture.Files.InspectChildNoFollow(parent, component);
                if (entry is null)
                {
                    Assert.False(Directory.Exists(Path.Combine(fixture.RootPath, relativePath)));
                    return;
                }

                Assert.Equal(PhysicalStoreEntryKind.Directory, entry.Kind);
                var next = fixture.Files.OpenDirectoryChildNoFollow(parent, component);
                current?.Dispose();
                current = next;
                parent = current;
            }

            Assert.Null(fixture.Files.InspectChildNoFollow(parent, version));
        }
        finally
        {
            current?.Dispose();
        }

        Assert.False(Directory.Exists(Path.Combine(fixture.RootPath, relativePath)));
    }

    private static string RootRelativePath(
        OverlappingPackageGraphProtectionTests.GraphUseFixture fixture,
        string path)
        => Path.GetRelativePath(fixture.RootPath, path).Replace(Path.DirectorySeparatorChar, '/');

    private static async Task WriteStateBodyMismatchAsync(string path, StoreStateRecord state)
    {
        await using var stream = new MemoryStream();
        await new StoreStateSerializer().WritePayloadAsync(stream, state, CancellationToken.None);
        await File.WriteAllBytesAsync(path, stream.ToArray());
    }

    private static JsonElement Candidate(JsonElement report, string packageId, string version)
        => report.GetProperty("candidates").EnumerateArray().Single(candidate =>
            String(candidate, "packageId") == packageId && String(candidate, "version") == version);

    private static string String(JsonElement value, string property)
        => value.GetProperty(property).GetString() ?? throw new Xunit.Sdk.XunitException(
            $"Expected JSON string property '{property}'.");

    private sealed record StateEvidence(PhysicalFileIdentity Identity, byte[] Bytes)
    {
        internal string Digest => Convert.ToHexString(SHA256.HashData(Bytes));
    }

    private sealed record InstallEvidence(PackageInstallIdentity Identity, string PayloadDigest);
    private sealed record ExpectedProtection(
        PackageInstallIdentity Install,
        HashSet<PackageStoreRetentionReason> Reasons);
    private sealed record CandidateNamespace(PhysicalFileIdentity ParentIdentity,
        PhysicalStoreNameSemantics ParentSemantics, PhysicalFileIdentity? OriginalIdentity,
        PhysicalFileIdentity? QuarantineIdentity, IReadOnlyDictionary<string, PhysicalFileIdentity> Entries,
        IReadOnlyDictionary<string, string> PayloadDigests);
    private sealed record ExecutionRequestFile(string OperationId, string Path);
    private sealed record ExecutionResponse(string Kind, JsonElement Message, PackageStoreParticipantExit Exit);

    private sealed class PausedExecution(
        PackageStoreParticipantProcess process,
        JsonElement checkpoint) : IAsyncDisposable
    {
        internal PackageStoreParticipantProcess Process { get; } = process;
        internal JsonElement Checkpoint { get; } = checkpoint;
        public ValueTask DisposeAsync() => Process.DisposeAsync();
    }
}
