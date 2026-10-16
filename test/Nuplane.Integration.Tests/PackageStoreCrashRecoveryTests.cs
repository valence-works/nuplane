using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Integration.Tests.Fixtures;
using Nuplane.Loading;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.Coordination.GraphUseSerialization;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Maintenance;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshAdmission_ReapsUseOnlyAfterLoadedProcessExitsAndPreservesProtection(bool terminateProcess)
        => await RunExitedGraphUseRecoveryScenarioAsync(terminateProcess, refuseForUnknownArtifact: false);

    [Fact]
    public async Task FreshAdmission_UnknownUseArtifactRefusesWithoutChangingStaleNamespace()
        => await RunExitedGraphUseRecoveryScenarioAsync(terminateProcess: true, refuseForUnknownArtifact: true);

    private static async Task RunExitedGraphUseRecoveryScenarioAsync(
        bool terminateProcess,
        bool refuseForUnknownArtifact)
    {
        var fixture = await GraphUseFixture.CreateAsync(PackageLoadMode.HostIntegrated);
        ServiceProvider? provider = null;
        PackageStoreParticipantProcess? participant = null;
        string? requestPath = null;
        Exception? primaryFailure = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            provider = fixture.CreateProvider("member-b");
            var nativeBefore = CaptureFixtureNativeEvidence(fixture);
            var operationId = Guid.NewGuid().ToString("N");
            requestPath = Path.Combine(Path.GetTempPath(), $"nuplane-recovery-{operationId}.json");
            var request = new GraphUseProcessRequest(operationId, fixture.RootPath,
                fixture.StatePaths["member-a"], fixture.Graphs["member-a"].Requests.ToArray(), fixture.CompleteRoster);
            await File.WriteAllTextAsync(requestPath,
                JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            participant = await PackageStoreParticipantProcess.StartGraphUseIdleAsync(
                operationId, requestPath, deadline.Token);
            using var loaded = await participant.ReadResponseAsync("loaded-idle", deadline.Token);
            Assert.Equal(operationId, loaded.RootElement.GetProperty("operationId").GetString());
            Assert.Equal(participant.ProcessId, loaded.RootElement.GetProperty("processId").GetInt32());
            Assert.False(loaded.RootElement.GetProperty("isCollectible").GetBoolean());
            Assert.True(loaded.RootElement.GetProperty("isCoordinatedParticipant").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(loaded.RootElement.GetProperty("contextIdentity").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(loaded.RootElement.GetProperty("contextName").GetString()));
            var assemblyNames = loaded.RootElement.GetProperty("assemblyNames").EnumerateArray()
                .Select(static item => item.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains("Root.First", assemblyNames);
            Assert.Contains("Shared.Dependency", assemblyNames);
            Assert.False(participant.HasExited);

            var admission = provider.GetRequiredService<IPackageStoreAdmission>();
            var live = await InspectUseStateAsync(admission, fixture, GraphUseRecordOwnershipState.Live, deadline.Token);
            Assert.Equal(participant.ProcessId, live.Record.DiagnosticProcessId);
            Assert.Equal(GraphUseLifetimeKind.OsExclusiveSentinel, live.Record.LifetimeKind);
            AssertGraphUseMatchesFixture(live.Record.GraphSnapshot, fixture, nativeBefore);

            var liveNamespace = CaptureUseNamespace(fixture);
            var livePersistence = CapturePersistenceEvidence(fixture);
            await AssertMembersRemainProtectedAsync(fixture, nativeBefore);
            AssertNativeEvidenceEqual(nativeBefore, CaptureFixtureNativeEvidence(fixture));
            await using (var liveAdmission = await admission.AcquireConfiguredRootOperationAsync(
                             PackageStoreAdmissionKind.Loading, deadline.Token))
            {
                Assert.Equal(PackageStoreAdmissionStatus.Enrolled, liveAdmission.Status);
                Assert.Equal(fixture.RootIdentity, liveAdmission.Root);
            }

            AssertUseNamespaceEqual(liveNamespace, CaptureUseNamespace(fixture));
            AssertPersistenceEvidenceEqual(livePersistence, CapturePersistenceEvidence(fixture));
            AssertNativeEvidenceEqual(nativeBefore, CaptureFixtureNativeEvidence(fixture));
            var stillLive = await InspectUseStateAsync(admission, fixture, GraphUseRecordOwnershipState.Live, deadline.Token);
            Assert.Equal(live.Record.UseId, stillLive.Record.UseId);
            Assert.Equal(live.Record.SentinelIdentity, stillLive.Record.SentinelIdentity);
            Assert.Equal(live.Record.PayloadDigest, stillLive.Record.PayloadDigest);

            var idlePersistence = CapturePersistenceEvidence(fixture);
            Assert.Equal(RootMembershipStatus.Complete, idlePersistence.LedgerStatus);
            Assert.Equal(fixture.RootIdentity, idlePersistence.LedgerRootIdentity);
            Assert.False(string.IsNullOrWhiteSpace(idlePersistence.LedgerDigest));
            await AssertMembersRemainProtectedAsync(fixture, nativeBefore);
            AssertNativeEvidenceEqual(nativeBefore, CaptureFixtureNativeEvidence(fixture));

            var exit = terminateProcess
                ? await participant.TerminateAsync(deadline.Token)
                : await participant.ReleaseOperationAsync(deadline.Token);
            if (terminateProcess)
                Assert.NotEqual(0, exit.ExitCode);
            else
                Assert.Equal(0, exit.ExitCode);

            var stale = await InspectUseStateAsync(admission, fixture, GraphUseRecordOwnershipState.Stale, deadline.Token);
            Assert.Equal(live.Record.UseId, stale.Record.UseId);
            AssertPersistenceEvidenceEqual(idlePersistence, CapturePersistenceEvidence(fixture));
            AssertNativeEvidenceEqual(nativeBefore, CaptureFixtureNativeEvidence(fixture));

            string? unknownArtifactName = null;
            if (refuseForUnknownArtifact)
            {
                unknownArtifactName = $"use-{Guid.NewGuid():N}.unknown";
                using var control = fixture.Files.OpenDirectoryChildNoFollow(
                    fixture.Root, RootMembershipRegistry.ControlDirectoryName);
                using var unknown = fixture.Files.CreateFileExclusiveAt(control, unknownArtifactName);
                fixture.Files.WriteNewControlFile(unknown, "unrecognized"u8.ToArray());
            }

            var namespaceBeforeAdmission = CaptureUseNamespace(fixture);
            Assert.NotEmpty(namespaceBeforeAdmission);
            provider.Dispose();
            provider = fixture.CreateProvider("member-b");
            admission = provider.GetRequiredService<IPackageStoreAdmission>();

            if (refuseForUnknownArtifact)
            {
                var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
                    await admission.AcquireConfiguredRootOperationAsync(
                        PackageStoreAdmissionKind.Reconciliation, deadline.Token));
                Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
                Assert.Contains(unknownArtifactName!, CaptureUseNamespace(fixture).Keys);
                AssertUseNamespaceEqual(namespaceBeforeAdmission, CaptureUseNamespace(fixture));
            }
            else
            {
                await using (var recovered = await admission.AcquireConfiguredRootOperationAsync(
                                 PackageStoreAdmissionKind.Reconciliation, deadline.Token))
                {
                    Assert.Equal(PackageStoreAdmissionStatus.Enrolled, recovered.Status);
                    Assert.Equal(fixture.RootIdentity, recovered.Root);
                    Assert.NotNull(recovered.Owner);
                }

                Assert.Empty((await InspectGraphUseRecordsAsync(admission, fixture)).Entries);

                var inspection = await CreateInspectionService(provider, fixture.Files)
                    .InspectAsync(0, deadline.Token);
                Assert.Equal(PackageStoreAdmissionStatus.Enrolled, inspection.AdmissionStatus);
                Assert.Equal(PackageStoreRetentionProtectionKnowledge.Known, inspection.ProtectionKnowledge);
                Assert.Equal(fixture.RootIdentity, inspection.Root);
                Assert.Equal(2, inspection.Members.Count);
                Assert.Empty(inspection.Uses);
                Assert.NotNull(inspection.Inventory);
                Assert.Equal(PackageStoreRetentionProtectionKnowledge.Known,
                    inspection.RetentionPlan!.ProtectionKnowledge);
            }

            AssertPersistenceEvidenceEqual(idlePersistence, CapturePersistenceEvidence(fixture));
            await AssertMembersRemainProtectedAsync(fixture, nativeBefore);
            AssertNativeEvidenceEqual(nativeBefore, CaptureFixtureNativeEvidence(fixture));
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
        try { provider?.Dispose(); }
        catch (Exception exception) { cleanupFailures.Add(exception); }
        try { fixture.Dispose(); }
        catch (Exception exception) { cleanupFailures.Add(exception); }
        if (requestPath is not null)
        {
            try { File.Delete(requestPath); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
        }

        if (primaryFailure is not null && cleanupFailures.Count > 0)
            throw new AggregateException("The graph-use recovery assertion and its cleanup both failed.",
                [primaryFailure, .. cleanupFailures]);
        if (primaryFailure is not null)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        if (cleanupFailures.Count > 0)
            throw new AggregateException("Graph-use recovery test cleanup failed.", cleanupFailures);
    }

    private static IReadOnlyDictionary<string, FileEvidence> CaptureUseNamespace(GraphUseFixture fixture)
    {
        using var control = fixture.Files.OpenDirectoryChildNoFollow(fixture.Root,
            RootMembershipRegistry.ControlDirectoryName);
        var enumeration = Assert.IsAssignableFrom<IPhysicalStoreDirectoryEnumerationFileSystem>(fixture.Files);
        var artifacts = enumeration.EnumerateChildNamesNoFollow(control, 4096)
            .Where(static name => name.StartsWith("use", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static name => name, StringComparer.Ordinal);
        var result = new Dictionary<string, FileEvidence>(StringComparer.Ordinal);
        foreach (var name in artifacts)
        {
            var entry = Assert.IsType<PhysicalStoreEntryInfo>(fixture.Files.InspectChildNoFollow(control, name));
            using var file = fixture.Files.OpenFileChildNoFollow(control, name, FileAccess.Read);
            result.Add(name, new FileEvidence(entry.Identity,
                fixture.Files.ReadControlFile(file, GraphUsePayloadSerializer.MaximumPayloadBytes)));
        }
        return result;
    }

    private static void AssertUseNamespaceEqual(
        IReadOnlyDictionary<string, FileEvidence> expected,
        IReadOnlyDictionary<string, FileEvidence> actual)
    {
        Assert.Equal(expected.Keys.OrderBy(static name => name, StringComparer.Ordinal),
            actual.Keys.OrderBy(static name => name, StringComparer.Ordinal));
        foreach (var name in expected.Keys)
        {
            Assert.Equal(expected[name].Identity, actual[name].Identity);
            Assert.Equal(expected[name].Bytes, actual[name].Bytes);
        }
    }
}
