using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Integration.Tests.Fixtures;
using Nuplane.Loading;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.GraphUseRecords;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Fact]
    public Task ParticipantLoadCrash_BetweenUsePublicationAndFirstLoaderInputIsRecoverable()
        => RunParticipantLoadCrashBoundaryAsync(beforeLoaderInput: true);

    [Fact]
    public Task ParticipantLoadCrash_AtFirstManagedAssemblyMetadataReadAttemptIsRecoverable()
        => RunParticipantLoadCrashBoundaryAsync(beforeLoaderInput: false);

    private static async Task RunParticipantLoadCrashBoundaryAsync(bool beforeLoaderInput)
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
            var nativeEvidence = CaptureFixtureNativeEvidence(fixture);
            var operationId = Guid.NewGuid().ToString("N");
            requestPath = Path.Combine(Path.GetTempPath(), $"nuplane-crash-boundary-{operationId}.json");
            var request = new GraphUseProcessRequest(operationId, fixture.RootPath,
                fixture.StatePaths["member-a"], fixture.Graphs["member-a"].Requests.ToArray(), fixture.CompleteRoster);
            await File.WriteAllTextAsync(requestPath,
                JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            participant = beforeLoaderInput
                ? await PackageStoreParticipantProcess.StartParticipantGraphUseBeforeLoaderInputAsync(
                    operationId, requestPath, deadline.Token)
                : await PackageStoreParticipantProcess.StartParticipantGraphUseBeforeFirstPeReadAsync(
                    operationId, requestPath, deadline.Token);

            using var boundary = await participant.ReadNextResponseAsync(deadline.Token);
            var expectedKind = beforeLoaderInput ? "before-loader-input" : "before-first-pe-read";
            Assert.Equal(expectedKind, boundary.RootElement.GetProperty("kind").GetString());
            Assert.Equal(operationId, boundary.RootElement.GetProperty("operationId").GetString());
            Assert.Equal(participant.ProcessId, boundary.RootElement.GetProperty("processId").GetInt32());
            Assert.False(participant.HasExited);
            Assert.False(boundary.RootElement.GetProperty("readDelegated").GetBoolean());
            Assert.Equal(0, boundary.RootElement.GetProperty("matchingPackageGraphContexts").GetInt32());

            var reportedUseId = Guid.ParseExact(boundary.RootElement.GetProperty("useId").GetString()!, "N");
            var reportedSnapshotId = Guid.ParseExact(boundary.RootElement.GetProperty("snapshotId").GetString()!, "N");
            Assert.NotEqual(Guid.Empty, reportedUseId);
            Assert.NotEqual(Guid.Empty, reportedSnapshotId);
            if (beforeLoaderInput)
            {
                Assert.Equal("before-load-mode-selection", boundary.RootElement.GetProperty("checkpointStage").GetString());
                Assert.Equal(0, boundary.RootElement.GetProperty("managedAssemblyReadAttempt").GetInt32());
                Assert.False(boundary.RootElement.GetProperty("activationGateCompleted").GetBoolean());
                Assert.Equal(JsonValueKind.Null, boundary.RootElement.GetProperty("assemblyPath").ValueKind);
            }
            else
            {
                Assert.Equal("before-assembly-name-get-assembly-name", boundary.RootElement.GetProperty("checkpointStage").GetString());
                Assert.Equal(1, boundary.RootElement.GetProperty("managedAssemblyReadAttempt").GetInt32());
                Assert.True(boundary.RootElement.GetProperty("activationGateCompleted").GetBoolean());
                var assemblyPath = Path.GetFullPath(boundary.RootElement.GetProperty("assemblyPath").GetString()!);
                Assert.Contains(fixture.Graphs["member-a"].Packages,
                    package => string.Equals(Path.GetFullPath(Path.Combine(package.InstallPath,
                        package.Id + ".dll")), assemblyPath, StringComparison.Ordinal));
            }

            var admission = provider.GetRequiredService<IPackageStoreAdmission>();
            var live = await InspectUseStateAsync(admission, fixture, GraphUseRecordOwnershipState.Live, deadline.Token);
            Assert.Equal(reportedUseId, live.Record.UseId);
            Assert.Equal(reportedSnapshotId, live.Record.GraphSnapshot.SnapshotId);
            Assert.Equal(participant.ProcessId, live.Record.DiagnosticProcessId);
            Assert.Equal(GraphUseLifetimeKind.OsExclusiveSentinel, live.Record.LifetimeKind);
            Assert.Equal(PackageGraphUseSnapshotState.Committed, live.Record.SnapshotState);
            Assert.Equal(fixture.Graphs["member-a"].Graph.GenerationId, live.Record.GraphSnapshot.GenerationId);
            AssertGraphUseMatchesFixture(live.Record.GraphSnapshot, fixture, nativeEvidence);

            var persistedState = CapturePersistenceEvidence(fixture);
            var useNamespace = CaptureUseNamespace(fixture);
            Assert.NotEmpty(useNamespace);
            await AssertMembersRemainProtectedAsync(fixture, nativeEvidence);
            AssertNativeEvidenceEqual(nativeEvidence, CaptureFixtureNativeEvidence(fixture));

            var exit = await participant.TerminateAsync(deadline.Token);
            Assert.NotEqual(0, exit.ExitCode);

            var stale = await InspectUseStateAsync(admission, fixture, GraphUseRecordOwnershipState.Stale, deadline.Token);
            Assert.Equal(reportedUseId, stale.Record.UseId);
            Assert.Equal(live.Record.PayloadDigest, stale.Record.PayloadDigest);
            Assert.Equal(live.Record.SentinelIdentity, stale.Record.SentinelIdentity);
            Assert.Equal(live.Record.GraphSnapshot.GenerationId, stale.Record.GraphSnapshot.GenerationId);
            AssertPersistenceEvidenceEqual(persistedState, CapturePersistenceEvidence(fixture));
            AssertUseNamespaceEqual(useNamespace, CaptureUseNamespace(fixture));
            await AssertMembersRemainProtectedAsync(fixture, nativeEvidence);
            AssertNativeEvidenceEqual(nativeEvidence, CaptureFixtureNativeEvidence(fixture));

            await provider.DisposeAsync();
            provider = fixture.CreateProvider("member-b");
            admission = provider.GetRequiredService<IPackageStoreAdmission>();
            await using (var recovered = await admission.AcquireConfiguredRootOperationAsync(
                             PackageStoreAdmissionKind.Reconciliation, deadline.Token))
            {
                Assert.Equal(PackageStoreAdmissionStatus.Enrolled, recovered.Status);
                Assert.Equal(fixture.RootIdentity, recovered.Root);
                Assert.NotNull(recovered.Owner);
            }

            Assert.Empty((await InspectGraphUseRecordsAsync(admission, fixture)).Entries);
            Assert.Empty(CaptureUseNamespace(fixture));
            AssertPersistenceEvidenceEqual(persistedState, CapturePersistenceEvidence(fixture));
            await AssertMembersRemainProtectedAsync(fixture, nativeEvidence);
            AssertNativeEvidenceEqual(nativeEvidence, CaptureFixtureNativeEvidence(fixture));
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
        if (provider is not null)
        {
            try { await provider.DisposeAsync(); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
        }
        try { fixture.Dispose(); }
        catch (Exception exception) { cleanupFailures.Add(exception); }
        if (requestPath is not null)
        {
            try { File.Delete(requestPath); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
        }

        if (primaryFailure is not null && cleanupFailures.Count > 0)
            throw new AggregateException("The graph-use crash-boundary assertion and cleanup both failed.",
                [primaryFailure, .. cleanupFailures]);
        if (primaryFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        if (cleanupFailures.Count > 0)
            throw new AggregateException("Graph-use crash-boundary cleanup failed.", cleanupFailures);
    }
}
