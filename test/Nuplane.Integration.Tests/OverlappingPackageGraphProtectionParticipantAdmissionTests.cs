using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nuplane.Admin;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Loading;
using Nuplane.Reconciliation;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Fact]
    public async Task TwoParticipantCompositions_ReadOfflineActiveLkgWhileUnlistedThirdIsRefusedBeforeSourceCallback()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        var serializer = new StoreStateSerializer();
        var pathA = fixture.StatePaths["member-a"];
        var pathB = fixture.StatePaths["member-b"];
        var stateA = await serializer.LoadAsync(pathA, CancellationToken.None);
        var stateB = await serializer.LoadAsync(pathB, CancellationToken.None);
        var bytesA = await File.ReadAllBytesAsync(pathA);
        var bytesB = await File.ReadAllBytesAsync(pathB);

        static void AssertOfflineActiveAndRecovery(StoreStateRecord state, string rootId)
        {
            Assert.Equal("1.0.0", state.ActiveVersionById[rootId]);
            Assert.Equal("1.0.0", state.LastKnownGoodById[rootId]);
            Assert.Equal("2.1.0", state.LastKnownGoodById["Shared.Dependency"]);
            var protection = Assert.IsType<PackageProtectionRecord>(state.ProtectionRecord);
            Assert.Equal(PackageProtectionClosureKnowledge.Known, protection.ActiveClosure.Knowledge);
            Assert.Equal(PackageProtectionClosureKnowledge.Known, protection.RecoverableClosure.Knowledge);
            Assert.Contains("Shared.Dependency", Assert.Single(protection.ActiveClosure.Graphs!).Nodes
                .Select(static node => node.Install.PackageId));
            Assert.Contains("Shared.Dependency", Assert.Single(protection.RecoverableClosure.Graphs!).Nodes
                .Select(static node => node.Install.PackageId));
        }

        AssertOfflineActiveAndRecovery(stateA, "Root.First");
        AssertOfflineActiveAndRecovery(stateB, "Root.Second");

        await using var providerA = fixture.CreateProvider("member-a", participant: true);
        await using var providerB = fixture.CreateProvider("member-b", participant: true);
        Assert.NotNull(providerA.GetRequiredService<IPackageStoreAdmission>());
        Assert.NotNull(providerB.GetRequiredService<IPackageStoreAdmission>());
        Assert.Null(providerA.GetService<INuplanePackageStoreMaintenanceOperations>());
        Assert.Null(providerB.GetService<INuplanePackageStoreMaintenanceOperations>());

        await using (var snapshotA = await NuplaneStore.OpenActivePackageSnapshotAsync(
                         providerA.GetRequiredService<NuplaneStoreSnapshotAccess>()))
        {
            Assert.Contains(snapshotA.Snapshot.Packages, static package => package.Id == "Root.First");
            Assert.Contains(snapshotA.Snapshot.Packages, static package => package.Id == "Shared.Dependency");
        }
        await using (var snapshotB = await NuplaneStore.OpenActivePackageSnapshotAsync(
                         providerB.GetRequiredService<NuplaneStoreSnapshotAccess>()))
        {
            Assert.Contains(snapshotB.Snapshot.Packages, static package => package.Id == "Root.Second");
            Assert.Contains(snapshotB.Snapshot.Packages, static package => package.Id == "Shared.Dependency");
        }

        AssertOfflineActiveAndRecovery(await serializer.LoadAsync(pathA, CancellationToken.None), "Root.First");
        AssertOfflineActiveAndRecovery(await serializer.LoadAsync(pathB, CancellationToken.None), "Root.Second");
        Assert.Equal(bytesA, await File.ReadAllBytesAsync(pathA));
        Assert.Equal(bytesB, await File.ReadAllBytesAsync(pathB));

        var unlistedStatePath = fixture.CreateStateSlot("state-third/store-state.json");
        await serializer.SaveAsync(unlistedStatePath, stateA, CancellationToken.None);
        var source = new CountingDesiredSource();
        using var unlistedProvider = fixture.CreateProvider("member-a", desiredRequests: [], participant: true,
            configureServices: services =>
            {
                services.Configure<StoreRegistryOptions>(options => options.StateFilePath = unlistedStatePath);
                services.RemoveAll<IDesiredPackageSource>();
                services.AddSingleton<IDesiredPackageSource>(source);
            });

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            unlistedProvider.GetRequiredService<IReconciliationService>()
                .TriggerAsync(ReconciliationTrigger.Manual("unlisted-third"), CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.IncompleteEnrollment, refusal.Reason);
        Assert.Equal(0, source.CallCount);
        Assert.Equal(bytesA, await File.ReadAllBytesAsync(pathA));
        Assert.Equal(bytesB, await File.ReadAllBytesAsync(pathB));
    }

    [Fact]
    public async Task HostIntegratedParticipantLoad_RefusesCorruptCatalogBeforeCallbacksOrContexts()
    {
        using var fixture = await GraphUseFixture.CreateAsync(PackageLoadMode.HostIntegrated);
        await using var provider = fixture.CreateProvider("member-b", participant: true);

        var corruptPeerPath = fixture.StatePaths["member-a"];
        var originalPeerBytes = await File.ReadAllBytesAsync(corruptPeerPath);
        var originalPeerIdentity = InspectStateFileIdentity(fixture.Files, corruptPeerPath);
        var originalPersistence = CapturePersistenceEvidence(fixture);
        var corruptPeerBytes = await WriteBodyMismatchedStateInPlaceAsync(corruptPeerPath);
        Assert.NotEqual(originalPeerBytes, corruptPeerBytes);
        Assert.Equal(originalPeerIdentity, InspectStateFileIdentity(fixture.Files, corruptPeerPath));

        var corruptPersistence = CapturePersistenceEvidence(fixture);
        var nativeEvidence = CaptureFixtureNativeEvidence(fixture);
        var useArtifacts = ReadUseArtifactNames(fixture);
        Assert.Empty(useArtifacts);
        var packageContexts = CapturePackageContextIdentities(fixture);
        var gate = new ActivationGateProbe();
        var options = new HostIntegratedLoadOptions();
        options.ActivationGates.Add(gate);

        try
        {
            var access = provider.GetRequiredService<NuplaneStoreSnapshotAccess>();
            var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
                NuplaneHostIntegratedLoader.LoadFromParticipantAsync(access, options));

            Assert.Equal(PackageStoreAdmissionReason.StateMismatch, refusal.Reason);
            Assert.Equal(0, gate.CallCount);
            Assert.Equal(packageContexts, CapturePackageContextIdentities(fixture));
            Assert.Equal(useArtifacts, ReadUseArtifactNames(fixture));
            AssertPersistenceEvidenceEqual(corruptPersistence, CapturePersistenceEvidence(fixture));
            AssertNativeEvidenceEqual(nativeEvidence, CaptureFixtureNativeEvidence(fixture));
        }
        finally
        {
            await WriteBytesInPlaceAsync(corruptPeerPath, originalPeerBytes);
        }

        Assert.Equal(originalPeerIdentity, InspectStateFileIdentity(fixture.Files, corruptPeerPath));
        AssertPersistenceEvidenceEqual(originalPersistence, CapturePersistenceEvidence(fixture));
        await using var maintenance = await provider.GetRequiredService<IPackageStoreAdmission>()
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
        Assert.NotNull(maintenance.Owner);
    }

    [Fact]
    public async Task HostIntegratedParticipantLoad_PreflightsIndependentGraphsBeforeAnyContextOrCallback()
    {
        using var fixture = await GraphUseFixture.CreateAsync(
            PackageLoadMode.HostIntegrated, includeIndependentGraph: true);
        var independent = Assert.IsType<ResolvedGraphFixture>(fixture.IndependentGraph);
        await using var provider = fixture.CreateProvider("member-a", participant: true);

        var published = await PublishProtectedStateAsync(provider, current =>
            fixture.BuildProtectedState("member-a", [fixture.Graphs["member-a"], independent], current,
                checked(current.ProtectionRecord!.Revision + 1), isUpdate: true));
        var protection = Assert.IsType<PackageProtectionRecord>(published.ProtectionRecord);
        Assert.Equal(2, protection.ActiveClosure.Graphs!.Count);
        Assert.Equal(2, published.ActiveGraphsByIdNormalized.Values
            .Count(static graph => graph.Status == GraphActivationStatus.Active));
        var orderedActiveGraphs = published.ActiveGraphsByIdNormalized.Values
            .Where(static graph => graph.Status == GraphActivationStatus.Active)
            .OrderBy(static graph => graph.NodePackageIds.Min(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Contains("Root.First", orderedActiveGraphs[0].NodePackageIds, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Root.Independent", orderedActiveGraphs[1].NodePackageIds, StringComparer.OrdinalIgnoreCase);
        var independentSnapshot = Assert.Single(protection.ActiveClosure.Graphs,
            graph => graph.Nodes.Any(node => string.Equals(node.Install.PackageId, "Root.Independent", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(["Root.Independent"], independentSnapshot.Nodes.Select(static node => node.Install.PackageId));

        var persistenceBefore = CapturePersistenceEvidence(fixture);
        var nativeEvidenceBefore = CaptureFixtureNativeEvidence(fixture);
        var artifactsBefore = ReadUseArtifactNames(fixture);
        Assert.Empty(artifactsBefore);
        var contextsBefore = CapturePackageContextIdentities(fixture);
        var gate = new ActivationGateProbe();
        var options = new HostIntegratedLoadOptions();
        options.ActivationGates.Add(gate);

        var installPath = independent.Packages.Single().InstallPath;
        var markerPath = Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName);
        var missingMarkerPath = markerPath + ".held-" + Guid.NewGuid().ToString("N");
        File.Move(markerPath, missingMarkerPath);
        try
        {
            var access = provider.GetRequiredService<NuplaneStoreSnapshotAccess>();
            var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
                NuplaneHostIntegratedLoader.LoadFromParticipantAsync(access, options));

            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
            Assert.Equal(0, gate.CallCount);
            Assert.Equal(contextsBefore, CapturePackageContextIdentities(fixture));
            Assert.Equal(artifactsBefore, ReadUseArtifactNames(fixture));
            AssertPersistenceEvidenceEqual(persistenceBefore, CapturePersistenceEvidence(fixture));
        }
        finally
        {
            File.Move(missingMarkerPath, markerPath);
        }

        AssertNativeEvidenceEqual(nativeEvidenceBefore, CaptureFixtureNativeEvidence(fixture));
        await using var maintenance = await provider.GetRequiredService<IPackageStoreAdmission>()
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
        Assert.NotNull(maintenance.Owner);
    }

    private static async Task<StoreStateRecord> PublishProtectedStateAsync(
        IServiceProvider provider,
        Func<StoreStateRecord, StoreStateRecord> createNextState)
    {
        var admission = provider.GetRequiredService<IPackageStoreAdmission>();
        await using var operation = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Reconciliation);
        using var borrow = operation.Owner!.Borrow();
        var registry = Assert.IsAssignableFrom<ICoordinatedStoreRegistry>(provider.GetRequiredService<IStoreRegistry>());
        var current = await registry.ReadCoordinatedStateAsync(borrow, CancellationToken.None);
        var next = createNextState(current);
        await registry.PersistCoordinatedActiveStateAsync(borrow, next, CancellationToken.None);
        var published = await registry.ReadCoordinatedStateAsync(borrow, CancellationToken.None);
        Assert.Equal(next.ProtectionRecord!.Revision, published.ProtectionRecord!.Revision);
        return published;
    }

    private static string[] CapturePackageContextIdentities(GraphUseFixture fixture)
    {
        var packageIds = fixture.Graphs.Values.SelectMany(static graph => graph.Packages)
            .Select(static package => package.Id)
            .Append("Root.Independent")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return AssemblyLoadContext.All
            .Where(context => context.Assemblies.Any(assembly => packageIds.Contains(assembly.GetName().Name ?? string.Empty)))
            .Select(context => $"{context.Name ?? "unnamed"}:{RuntimeHelpers.GetHashCode(context):x8}")
            .OrderBy(static identity => identity, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task WriteBytesInPlaceAsync(string path, byte[] bytes)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
        file.SetLength(0);
        await file.WriteAsync(bytes);
        await file.FlushAsync();
    }

    private sealed class CountingDesiredSource : IPackagePathIndependentDesiredPackageSource
    {
        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult<IReadOnlyList<PackageRequest>>([]);
        }
    }

    private sealed class ActivationGateProbe : IPackagePathIndependentActivationGate
    {
        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<PackageActivationGateResult> EvaluateAsync(
            PackageActivationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult(PackageActivationGateResult.Allow);
        }
    }
}
