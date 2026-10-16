using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Integration.Tests.Fixtures;
using Nuplane.Loading;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task HostIntegratedGraphUse_BecomesStaleOnlyAfterIdleProcessExit(bool terminateProcess) =>
        RunGraphUseProcessExitAsync(terminateProcess, ParticipantGraphUseProcessMode.OrdinaryReconciliation);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task HostIntegratedParticipantLoad_BecomesStaleOnlyAfterIdleProcessExit(bool terminateProcess) =>
        RunGraphUseProcessExitAsync(terminateProcess, ParticipantGraphUseProcessMode.ParticipantRepeat);

    [Fact]
    public Task HostIntegratedParticipantLoad_ReusesAcrossEquivalentReorderedRoster() =>
        RunGraphUseProcessExitAsync(false, ParticipantGraphUseProcessMode.ParticipantRepeatReorderedRoster);

    [Fact]
    public Task HostIntegratedParticipantLoad_RejectsUnscopedGateAndReleasesLease() =>
        RunGraphUseProcessExitAsync(false, ParticipantGraphUseProcessMode.ParticipantUnscopedGate);

    [Fact]
    public Task HostIntegratedParticipantLoad_RunsScopedGateAfterSnapshotRelease() =>
        RunGraphUseProcessExitAsync(false, ParticipantGraphUseProcessMode.ParticipantScopedGate);

    [Fact]
    public async Task HostIntegratedParticipantLoad_RejectsChangedPersistedIdentityOnRepeat()
    {
        var fixture = await GraphUseFixture.CreateAsync(
            PackageLoadMode.HostIntegrated, includeFreshMemberAGraph: true);
        ServiceProvider? parentProvider = null;
        PackageStoreParticipantProcess? participant = null;
        string? requestPath = null;
        Exception? primaryFailure = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            parentProvider = fixture.CreateProvider("member-a");
            var nativeEvidenceBefore = CaptureFixtureNativeEvidence(fixture);
            var operationId = Guid.NewGuid().ToString("N");
            requestPath = Path.Combine(Path.GetTempPath(), $"nuplane-graph-use-repeat-{operationId}.json");
            var request = new GraphUseProcessRequest(operationId, fixture.RootPath,
                fixture.StatePaths["member-a"], fixture.Graphs["member-a"].Requests.ToArray(), fixture.CompleteRoster);
            await File.WriteAllTextAsync(requestPath,
                JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            participant = await PackageStoreParticipantProcess.StartParticipantGraphUseIdentityMismatchAsync(
                operationId, requestPath, deadline.Token);
            using var loaded = await participant.ReadResponseAsync("loaded-idle", deadline.Token);
            Assert.Equal(participant.ProcessId, loaded.RootElement.GetProperty("processId").GetInt32());
            Assert.False(loaded.RootElement.GetProperty("isCollectible").GetBoolean());
            Assert.True(loaded.RootElement.GetProperty("isCoordinatedParticipant").GetBoolean());
            Assert.False(participant.HasExited);
            var assemblyNames = loaded.RootElement.GetProperty("assemblyNames").EnumerateArray()
                .Select(static item => item.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains("Root.First", assemblyNames);
            Assert.Contains("Shared.Dependency", assemblyNames);

            var admission = parentProvider.GetRequiredService<IPackageStoreAdmission>();
            var originalLive = await InspectUseStateAsync(admission, fixture, GraphUseRecordOwnershipState.Live, deadline.Token);
            Assert.Equal(participant.ProcessId, originalLive.Record.DiagnosticProcessId);
            Assert.Equal(fixture.Graphs["member-a"].Graph.GenerationId, originalLive.Record.GraphSnapshot.GenerationId);
            AssertGraphUseMatchesFixture(originalLive.Record.GraphSnapshot, fixture, nativeEvidenceBefore);

            var originalState = await new StoreStateSerializer().LoadAsync(
                fixture.StatePaths["member-a"], CancellationToken.None);
            var originalCandidate = Assert.Single(
                Assert.IsType<PackageProtectionRecord>(originalState.ProtectionRecord).ActiveClosure.Graphs!);
            var replacementGraph = Assert.IsType<ResolvedGraphFixture>(fixture.FreshMemberAGraph);
            Assert.Equal(originalCandidate.GraphId, replacementGraph.Graph.GraphId);
            Assert.NotEqual(originalCandidate.GenerationId, replacementGraph.Graph.GenerationId);
            Assert.Equal(originalCandidate.Nodes.Select(static node => (node.Install.PackageId, node.Install.Version))
                    .OrderBy(static item => item.PackageId, StringComparer.OrdinalIgnoreCase),
                replacementGraph.Graph.Nodes.Select(static node => (node.PackageId, node.Version))
                    .OrderBy(static item => item.PackageId, StringComparer.OrdinalIgnoreCase));

            var replacementState = await PublishProtectedStateAsync(parentProvider, current =>
            {
                Assert.Equal(originalState.ProtectionRecord!.Revision, current.ProtectionRecord!.Revision);
                return fixture.BuildProtectedState("member-a", replacementGraph, current,
                    checked(current.ProtectionRecord.Revision + 1), isUpdate: true);
            });
            var replacementCandidate = Assert.Single(
                Assert.IsType<PackageProtectionRecord>(replacementState.ProtectionRecord).ActiveClosure.Graphs!);
            Assert.NotEqual(originalCandidate.SnapshotId, replacementCandidate.SnapshotId);
            Assert.NotEqual(originalCandidate.GenerationId, replacementCandidate.GenerationId);
            Assert.Equal(originalCandidate.Nodes.Select(static node => (node.Install.PackageId, node.Install.Version))
                    .OrderBy(static item => item.PackageId, StringComparer.OrdinalIgnoreCase),
                replacementCandidate.Nodes.Select(static node => (node.Install.PackageId, node.Install.Version))
                    .OrderBy(static item => item.PackageId, StringComparer.OrdinalIgnoreCase));

            var publishedPersistence = CapturePersistenceEvidence(fixture);
            AssertNativeEvidenceEqual(nativeEvidenceBefore, CaptureFixtureNativeEvidence(fixture));
            await AssertMembersRemainProtectedAsync(fixture, nativeEvidenceBefore);

            using (var repeat = await participant.RequestIdentityRepeatAsync(deadline.Token))
            {
                Assert.Equal(operationId, repeat.RootElement.GetProperty("operationId").GetString());
                Assert.Equal(participant.ProcessId, repeat.RootElement.GetProperty("processId").GetInt32());
                Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority.ToString(),
                    repeat.RootElement.GetProperty("reason").GetString());
                Assert.False(repeat.RootElement.GetProperty("activationGateEvaluated").GetBoolean());
                Assert.Equal(1, repeat.RootElement.GetProperty("matchingPackageGraphContexts").GetInt32());
                Assert.Equal(loaded.RootElement.GetProperty("contextIdentity").GetString(),
                    repeat.RootElement.GetProperty("contextIdentity").GetString());
            }

            var stillLive = await InspectUseStateAsync(admission, fixture, GraphUseRecordOwnershipState.Live, deadline.Token);
            Assert.Equal(originalLive.Record.UseId, stillLive.Record.UseId);
            Assert.Equal(originalLive.Record.PayloadDigest, stillLive.Record.PayloadDigest);
            Assert.Equal(originalLive.Record.SentinelIdentity, stillLive.Record.SentinelIdentity);
            Assert.Equal(originalLive.Record.GraphSnapshot.SnapshotId, stillLive.Record.GraphSnapshot.SnapshotId);
            Assert.Equal(originalLive.Record.GraphSnapshot.GenerationId, stillLive.Record.GraphSnapshot.GenerationId);
            AssertPersistenceEvidenceEqual(publishedPersistence, CapturePersistenceEvidence(fixture));
            AssertNativeEvidenceEqual(nativeEvidenceBefore, CaptureFixtureNativeEvidence(fixture));

            var exit = await participant.ReleaseOperationAsync(deadline.Token);
            Assert.Equal(0, exit.ExitCode);
            var stale = await InspectUseStateAsync(admission, fixture, GraphUseRecordOwnershipState.Stale, deadline.Token);
            Assert.Equal(originalLive.Record.UseId, stale.Record.UseId);
            Assert.Equal(originalLive.Record.PayloadDigest, stale.Record.PayloadDigest);
            Assert.Equal(originalLive.Record.SentinelIdentity, stale.Record.SentinelIdentity);
            Assert.Equal(originalLive.Record.GraphSnapshot.GenerationId, stale.Record.GraphSnapshot.GenerationId);
            AssertPersistenceEvidenceEqual(publishedPersistence, CapturePersistenceEvidence(fixture));
            AssertNativeEvidenceEqual(nativeEvidenceBefore, CaptureFixtureNativeEvidence(fixture));
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        if (participant is not null)
        {
            try { await participant.DisposeAsync(); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
        }
        try
        {
            if (parentProvider is not null)
                await parentProvider.DisposeAsync();
        }
        catch (Exception exception) { cleanupFailures.Add(exception); }
        try { fixture.Dispose(); }
        catch (Exception exception) { cleanupFailures.Add(exception); }
        if (requestPath is not null)
        {
            try { File.Delete(requestPath); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
        }

        if (primaryFailure is not null && cleanupFailures.Count > 0)
            throw new AggregateException("The identity-repeat assertion and its cleanup both failed.",
                [primaryFailure, .. cleanupFailures]);
        if (primaryFailure is not null)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        if (cleanupFailures.Count > 0)
            throw new AggregateException("Identity-repeat test cleanup failed.", cleanupFailures);
    }

    private async Task RunGraphUseProcessExitAsync(bool terminateProcess, ParticipantGraphUseProcessMode mode)
    {
        var fixture = await GraphUseFixture.CreateAsync(PackageLoadMode.HostIntegrated);
        ServiceProvider? parentProvider = null;
        PackageStoreParticipantProcess? participant = null;
        string? requestPath = null;
        Exception? primaryFailure = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            parentProvider = fixture.CreateProvider("member-b");
            var inventoryBefore = CaptureFixtureNativeEvidence(fixture);
            var operationId = Guid.NewGuid().ToString("N");
            requestPath = Path.Combine(Path.GetTempPath(), $"nuplane-graph-use-{operationId}.json");
            var request = new GraphUseProcessRequest(operationId, fixture.RootPath,
                fixture.StatePaths["member-a"], fixture.Graphs["member-a"].Requests.ToArray(), fixture.CompleteRoster);
            await File.WriteAllTextAsync(requestPath,
                JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            participant = mode switch
            {
                ParticipantGraphUseProcessMode.OrdinaryReconciliation => await PackageStoreParticipantProcess.StartGraphUseIdleAsync(
                    operationId, requestPath, deadline.Token),
                ParticipantGraphUseProcessMode.ParticipantRepeat => await PackageStoreParticipantProcess.StartParticipantGraphUseIdleAsync(
                    operationId, requestPath, deadline.Token),
                ParticipantGraphUseProcessMode.ParticipantRepeatReorderedRoster => await PackageStoreParticipantProcess.StartParticipantGraphUseReorderedRosterAsync(
                    operationId, requestPath, deadline.Token),
                ParticipantGraphUseProcessMode.ParticipantUnscopedGate => await PackageStoreParticipantProcess.StartParticipantGraphUseUnscopedGateAsync(
                    operationId, requestPath, deadline.Token),
                ParticipantGraphUseProcessMode.ParticipantScopedGate => await PackageStoreParticipantProcess.StartParticipantGraphUseScopedGateAsync(
                    operationId, requestPath, deadline.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };
            var admission = parentProvider.GetRequiredService<IPackageStoreAdmission>();
            if (mode == ParticipantGraphUseProcessMode.ParticipantUnscopedGate)
            {
                using var refused = await participant.ReadResponseAsync("refused-idle", deadline.Token);
                Assert.Equal(operationId, refused.RootElement.GetProperty("operationId").GetString());
                Assert.Equal(participant.ProcessId, refused.RootElement.GetProperty("processId").GetInt32());
                Assert.False(refused.RootElement.GetProperty("activationGateEvaluated").GetBoolean());
                Assert.Equal(0, refused.RootElement.GetProperty("matchingPackageGraphContexts").GetInt32());
                Assert.False(participant.HasExited);

                var staleBeforeExit = await InspectUseStateAsync(admission, fixture, GraphUseRecordOwnershipState.Stale, deadline.Token);
                Assert.Equal(PackageGraphUseSnapshotState.Committed, staleBeforeExit.Record.SnapshotState);
                var refusedPersistence = CapturePersistenceEvidence(fixture);
                Assert.Equal(RootMembershipStatus.Complete, refusedPersistence.LedgerStatus);
                Assert.Equal(fixture.RootIdentity, refusedPersistence.LedgerRootIdentity);
                await AssertMembersRemainProtectedAsync(fixture, inventoryBefore);
                AssertNativeEvidenceEqual(inventoryBefore, CaptureFixtureNativeEvidence(fixture));

                var refusalExit = await participant.ReleaseOperationAsync(deadline.Token);
                Assert.Equal(0, refusalExit.ExitCode);
                var staleAfterExit = await InspectUseStateAsync(admission, fixture, GraphUseRecordOwnershipState.Stale, deadline.Token);
                Assert.Equal(staleBeforeExit.Record.UseId, staleAfterExit.Record.UseId);
                Assert.Equal(staleBeforeExit.Record.PayloadDigest, staleAfterExit.Record.PayloadDigest);
                Assert.Equal(staleBeforeExit.Record.SentinelIdentity, staleAfterExit.Record.SentinelIdentity);
                AssertPersistenceEvidenceEqual(refusedPersistence, CapturePersistenceEvidence(fixture));
                await AssertMembersRemainProtectedAsync(fixture, inventoryBefore);
                AssertNativeEvidenceEqual(inventoryBefore, CaptureFixtureNativeEvidence(fixture));
            }
            else
            {
                using var loaded = await participant.ReadResponseAsync("loaded-idle", deadline.Token);
                Assert.Equal(operationId, loaded.RootElement.GetProperty("operationId").GetString());
                Assert.Equal(participant.ProcessId, loaded.RootElement.GetProperty("processId").GetInt32());
                Assert.False(loaded.RootElement.GetProperty("isCollectible").GetBoolean());
                Assert.True(loaded.RootElement.GetProperty("isCoordinatedParticipant").GetBoolean());
                Assert.False(string.IsNullOrWhiteSpace(loaded.RootElement.GetProperty("contextIdentity").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(loaded.RootElement.GetProperty("contextName").GetString()));
                Assert.False(participant.HasExited);
                if (mode == ParticipantGraphUseProcessMode.ParticipantRepeatReorderedRoster)
                    Assert.True(loaded.RootElement.GetProperty("didParticipantRepeatCompositionAcceptance").GetBoolean());
                if (mode == ParticipantGraphUseProcessMode.ParticipantScopedGate)
                    Assert.True(loaded.RootElement.GetProperty("didActivationGateProbeLiveUse").GetBoolean());
                var assemblyNames = loaded.RootElement.GetProperty("assemblyNames").EnumerateArray()
                    .Select(static item => item.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                Assert.Contains("Root.First", assemblyNames);
                Assert.Contains("Shared.Dependency", assemblyNames);

                var live = await InspectUseStateAsync(admission, fixture, GraphUseRecordOwnershipState.Live, deadline.Token);
                Assert.Equal(participant.ProcessId, live.Record.DiagnosticProcessId);
                Assert.Equal(GraphUseLifetimeKind.OsExclusiveSentinel, live.Record.LifetimeKind);
                Assert.Equal(PackageGraphUseSnapshotState.Committed, live.Record.SnapshotState);
                AssertGraphUseMatchesFixture(live.Record.GraphSnapshot, fixture, inventoryBefore);

                var idlePersistence = CapturePersistenceEvidence(fixture);
                Assert.Equal(RootMembershipStatus.Complete, idlePersistence.LedgerStatus);
                Assert.Equal(fixture.RootIdentity, idlePersistence.LedgerRootIdentity);
                Assert.False(string.IsNullOrWhiteSpace(idlePersistence.LedgerDigest));
                await AssertMembersRemainProtectedAsync(fixture, inventoryBefore);
                AssertNativeEvidenceEqual(inventoryBefore, CaptureFixtureNativeEvidence(fixture));

                var exit = terminateProcess
                    ? await participant.TerminateAsync(deadline.Token)
                    : await participant.ReleaseOperationAsync(deadline.Token);
                if (terminateProcess)
                    Assert.NotEqual(0, exit.ExitCode);
                else
                    Assert.Equal(0, exit.ExitCode);

                var stale = await InspectUseStateAsync(admission, fixture, GraphUseRecordOwnershipState.Stale, deadline.Token);
                Assert.Equal(live.Record.UseId, stale.Record.UseId);
                Assert.Equal(live.Record.PayloadDigest, stale.Record.PayloadDigest);
                Assert.Equal(live.Record.SentinelIdentity, stale.Record.SentinelIdentity);
                Assert.Equal(live.Record.GraphSnapshot.GenerationId, stale.Record.GraphSnapshot.GenerationId);
                AssertPersistenceEvidenceEqual(idlePersistence, CapturePersistenceEvidence(fixture));
                await AssertMembersRemainProtectedAsync(fixture, inventoryBefore);
                AssertNativeEvidenceEqual(inventoryBefore, CaptureFixtureNativeEvidence(fixture));
            }
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        if (participant is not null)
        {
            try { await participant.DisposeAsync(); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
        }

        try { parentProvider?.Dispose(); }
        catch (Exception exception) { cleanupFailures.Add(exception); }
        try { fixture.Dispose(); }
        catch (Exception exception) { cleanupFailures.Add(exception); }
        if (requestPath is not null)
        {
            try { File.Delete(requestPath); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
        }

        if (primaryFailure is not null && cleanupFailures.Count > 0)
            throw new AggregateException("The process-exit assertion and its cleanup both failed.",
                [primaryFailure, .. cleanupFailures]);
        if (primaryFailure is not null)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        if (cleanupFailures.Count > 0)
            throw new AggregateException("Process-exit test cleanup failed.", cleanupFailures);
    }

    private static async Task<GraphUseRecordInspectionEntry> InspectUseStateAsync(
        IPackageStoreAdmission admission,
        GraphUseFixture fixture,
        GraphUseRecordOwnershipState expectedState,
        CancellationToken cancellationToken)
    {
        var last = default(GraphUseRecordInspectionEntry);
        for (var attempt = 0; attempt < 80; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inspection = await InspectGraphUseRecordsAsync(admission, fixture);
            var entry = Assert.Single(inspection.Entries);
            if (entry.OwnershipState == expectedState)
                return entry;
            last = entry;
            await Task.Delay(100, cancellationToken);
        }

        throw new Xunit.Sdk.XunitException(
            $"The same graph-use record did not become {expectedState} after bounded parent inspection; last state was {last?.OwnershipState}.");
    }

    private static void AssertGraphUseMatchesFixture(
        ProtectedGraphSnapshot snapshot,
        GraphUseFixture fixture,
        IReadOnlyDictionary<string, NativeInstallEvidence> nativeEvidence)
    {
        var expectedPackages = fixture.Graphs["member-a"].Packages;
        Assert.Equal(expectedPackages.Count, snapshot.Nodes.Count);
        Assert.Equal(expectedPackages.Select(static package => package.Id).OrderBy(static id => id, StringComparer.OrdinalIgnoreCase),
            snapshot.Nodes.Select(static node => node.Install.PackageId).OrderBy(static id => id, StringComparer.OrdinalIgnoreCase));
        foreach (var node in snapshot.Nodes)
        {
            var evidence = RequireNativeEvidence(nativeEvidence, node.Install.RootRelativeInstallPath);
            Assert.Equal(evidence.Identity, node.Install);
        }
        Assert.Equal(fixture.RootIdentity, Assert.Single(snapshot.Roots));
    }

    private static async Task AssertMembersRemainProtectedAsync(
        GraphUseFixture fixture,
        IReadOnlyDictionary<string, NativeInstallEvidence> nativeEvidence)
    {
        var serializer = new StoreStateSerializer();
        foreach (var (memberId, graphKey) in new[] { ("member-a", "member-a"), ("member-b", "member-b") })
        {
            var state = await serializer.LoadAsync(fixture.StatePaths[memberId], CancellationToken.None);
            var packages = fixture.Graphs[graphKey].Packages;
            Assert.Equal(packages.Count, state.ActiveVersionById.Count);
            Assert.Equal(packages.Count, state.LastKnownGoodById.Count);
            foreach (var package in packages)
            {
                Assert.Equal(package.Version, state.ActiveVersionById[package.Id]);
                Assert.Equal(package.Version, state.LastKnownGoodById[package.Id]);
            }

            var protection = Assert.IsType<PackageProtectionRecord>(state.ProtectionRecord);
            Assert.Equal(PackageProtectionClosureKnowledge.Known, protection.ActiveClosure.Knowledge);
            Assert.Equal(PackageProtectionClosureKnowledge.Known, protection.RecoverableClosure.Knowledge);
            AssertProtectedGraphsMatchFixture(protection.ActiveClosure.Graphs!, packages, nativeEvidence);
            AssertProtectedGraphsMatchFixture(protection.RecoverableClosure.Graphs!, packages, nativeEvidence);
        }
    }

    private static void AssertProtectedGraphsMatchFixture(
        IReadOnlyList<ProtectedGraphSnapshot> graphs,
        IReadOnlyList<ResolvedPackage> expectedPackages,
        IReadOnlyDictionary<string, NativeInstallEvidence> nativeEvidence)
    {
        Assert.NotEmpty(graphs);
        var expectedIdentities = expectedPackages.Select(package =>
        {
            var evidence = nativeEvidence.Values.Single(candidate =>
                string.Equals(candidate.Identity.PackageId, package.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Identity.Version, package.Version, StringComparison.OrdinalIgnoreCase));
            return evidence.Identity;
        }).OrderBy(static identity => identity.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static identity => identity.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var graph in graphs)
        {
            Assert.Equal(expectedIdentities,
                graph.Nodes.Select(static node => node.Install)
                    .OrderBy(static identity => identity.PackageId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static identity => identity.Version, StringComparer.OrdinalIgnoreCase));
        }
    }

    private static NativeInstallEvidence RequireNativeEvidence(
        IReadOnlyDictionary<string, NativeInstallEvidence> nativeEvidence,
        string installPath)
        => nativeEvidence.TryGetValue(installPath, out var evidence)
            ? evidence
            : throw new Xunit.Sdk.XunitException($"No native fixture evidence exists for {installPath}.");

    private static PersistenceEvidence CapturePersistenceEvidence(GraphUseFixture fixture)
    {
        using var control = fixture.Files.OpenDirectoryChildNoFollow(fixture.Root, RootMembershipRegistry.ControlDirectoryName);
        var ledgerEntry = Assert.IsType<PhysicalStoreEntryInfo>(
            fixture.Files.InspectChildNoFollow(control, RootMembershipRegistry.LedgerName));
        var ledgerBytes = File.ReadAllBytes(Path.Combine(fixture.RootPath,
            RootMembershipRegistry.ControlDirectoryName, RootMembershipRegistry.LedgerName));
        var ledger = fixture.Registry.ReadCandidate(fixture.Root);
        var states = new Dictionary<string, FileEvidence>(StringComparer.Ordinal);
        foreach (var pair in fixture.StatePaths)
        {
            using var parent = OwnedProcessDirectory.Open(fixture.Files, Path.GetDirectoryName(pair.Value)!);
            var basename = Path.GetFileName(pair.Value);
            var stateEntry = Assert.IsType<PhysicalStoreEntryInfo>(fixture.Files.InspectChildNoFollow(parent, basename));
            states.Add(pair.Key, new FileEvidence(stateEntry.Identity, File.ReadAllBytes(pair.Value)));
        }

        return new PersistenceEvidence(fixture.Files.InspectHandle(fixture.Root).Identity,
            ledger.RootIdentity, ledgerEntry.Identity, ledgerBytes, ledger.Status, ledger.LedgerDigest,
            ledger.EnrollmentEpoch, states);
    }

    private static void AssertPersistenceEvidenceEqual(PersistenceEvidence expected, PersistenceEvidence actual)
    {
        Assert.Equal(expected.RootHandleIdentity, actual.RootHandleIdentity);
        Assert.Equal(expected.LedgerRootIdentity, actual.LedgerRootIdentity);
        Assert.Equal(expected.LedgerIdentity, actual.LedgerIdentity);
        Assert.Equal(expected.LedgerBytes, actual.LedgerBytes);
        Assert.Equal(expected.LedgerStatus, actual.LedgerStatus);
        Assert.Equal(expected.LedgerDigest, actual.LedgerDigest);
        Assert.Equal(expected.EnrollmentEpoch, actual.EnrollmentEpoch);
        Assert.Equal(expected.States.Keys.OrderBy(static key => key, StringComparer.Ordinal),
            actual.States.Keys.OrderBy(static key => key, StringComparer.Ordinal));
        foreach (var key in expected.States.Keys)
        {
            Assert.Equal(expected.States[key].Identity, actual.States[key].Identity);
            Assert.Equal(expected.States[key].Bytes, actual.States[key].Bytes);
        }
    }

    private static void AssertNativeEvidenceEqual(
        IReadOnlyDictionary<string, NativeInstallEvidence> expected,
        IReadOnlyDictionary<string, NativeInstallEvidence> actual)
    {
        Assert.Equal(expected.Keys.OrderBy(static key => key, StringComparer.Ordinal),
            actual.Keys.OrderBy(static key => key, StringComparer.Ordinal));
        foreach (var key in expected.Keys)
            Assert.Equal(expected[key], actual[key]);
    }

    private enum ParticipantGraphUseProcessMode
    {
        OrdinaryReconciliation,
        ParticipantRepeat,
        ParticipantRepeatReorderedRoster,
        ParticipantUnscopedGate,
        ParticipantScopedGate
    }

    private sealed record GraphUseProcessRequest(
        string OperationId,
        string RootPath,
        string StateFilePath,
        PackageRequest[] DesiredRequests,
        Nuplane.Builder.PackageStoreMemberDeclaration[] CompleteRoster);

    private sealed record FileEvidence(PhysicalFileIdentity Identity, byte[] Bytes);

    private sealed record PersistenceEvidence(
        PhysicalFileIdentity RootHandleIdentity,
        PhysicalRootIdentity LedgerRootIdentity,
        PhysicalFileIdentity LedgerIdentity,
        byte[] LedgerBytes,
        RootMembershipStatus LedgerStatus,
        string LedgerDigest,
        long EnrollmentEpoch,
        IReadOnlyDictionary<string, FileEvidence> States);
}
