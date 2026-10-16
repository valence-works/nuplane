using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Loading;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Fact]
    public async Task ParticipantDeferredNativeExport_RetainsRetiredRootContextAndGraphUseUntilReleased()
    {
        string? nativePath = null;
        var fixture = await GraphUseFixture.CreateAsync(
            beforeSharedDependencyCompletionMarker: packagePath =>
                nativePath = NativeLibraryFixtureCompiler.CompileInto(Path.Combine(packagePath, "runtimes",
                    RuntimeInformation.RuntimeIdentifier, "native")),
            emitNativeProbeOnRootSecondV1: true);
        ServiceProvider? provider = null;
        Exception? failure = null;
        try
        {
            Assert.NotNull(nativePath);
            var nativeDigest = HashFile(nativePath!);
            provider = fixture.CreateProvider("member-b",
                desiredRequests: fixture.Graphs["member-b-v2"].Requests,
                participant: true);

            // Finish the reflected-Type scope before checking actual weak death. A null assignment
            // inside that scope does not guarantee every JIT/async temporary stopped retaining it.
            var proof = await AssertRetainedNativeExportAsync(fixture, provider, nativePath!, nativeDigest);
            await WaitForWeakTargetDeathAsync(proof.Context);
            await WaitForGraphUseSentinelReleaseAsync(fixture, proof.UseId, proof.SentinelIdentity);

            var releasedUses = await InspectGraphUseRecordsAsync(provider.GetRequiredService<IPackageStoreAdmission>(), fixture);
            var releasedUse = Assert.Single(releasedUses.Entries, entry => entry.Record.UseId == proof.UseId);
            Assert.Equal(GraphUseRecordOwnershipState.Stale, releasedUse.OwnershipState);
            Assert.Equal(proof.SentinelIdentity, releasedUse.Record.SentinelIdentity);
            Assert.Equal(proof.UseRecordBytes, await File.ReadAllBytesAsync(proof.UseRecordPath));
            Assert.Equal(nativeDigest, HashFile(nativePath!));
            Assert.Equal(proof.InstallEvidence.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
                CaptureFixtureNativeEvidence(fixture).OrderBy(static pair => pair.Key, StringComparer.Ordinal));
            foreach (var pair in proof.StateBytes)
                Assert.Equal(pair.Value, await File.ReadAllBytesAsync(fixture.StatePaths[pair.Key]));
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            await CompleteNativeLifetimeTestAsync(fixture, provider, failure);
        }
    }

    [Fact]
    public async Task ParticipantNativeExport_LoadedBeforeUnload_RemainsCallableAfterUnloadAndReleasesAfterDeath()
    {
        string? nativePath = null;
        var fixture = await GraphUseFixture.CreateAsync(
            beforeSharedDependencyCompletionMarker: packagePath =>
                nativePath = NativeLibraryFixtureCompiler.CompileInto(Path.Combine(packagePath, "runtimes",
                    RuntimeInformation.RuntimeIdentifier, "native")),
            emitNativeProbeOnRootSecondV1: true);
        ServiceProvider? provider = null;
        Exception? failure = null;
        try
        {
            Assert.NotNull(nativePath);
            var nativeDigest = HashFile(nativePath!);
            provider = fixture.CreateProvider("member-b",
                desiredRequests: fixture.Graphs["member-b-v2"].Requests,
                participant: true);

            var proof = await LoadNativeExportBeforeAndCallAfterUnloadAsync(provider, fixture);
            await WaitForWeakTargetDeathAsync(proof.Context);
            await WaitForGraphUseSentinelReleaseAsync(fixture, proof.UseId, proof.SentinelIdentity);

            Assert.False(IsWeakTargetAlive(proof.Context));
            Assert.Equal(nativeDigest, HashFile(nativePath!));
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            await CompleteNativeLifetimeTestAsync(fixture, provider, failure);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<PreloadedNativeLifetimeProof> LoadNativeExportBeforeAndCallAfterUnloadAsync(
        ServiceProvider provider,
        GraphUseFixture fixture)
    {
        var loader = provider.GetRequiredService<PackageLoader>();
        PackageGraphUseLeaseOwner? owner = null;
        Exception? failure = null;
        try
        {
            var graph = fixture.Graphs["member-b"];
            owner = await AcquireGraphUseAsync(provider, graph);
            var load = await provider.GetRequiredService<IScopedPackageLoader>().EnsureGraphLoadedAsync(
                [graph.CreateEnvelope(owner)], [], CancellationToken.None);
            Assert.Empty(load.FailedByPackageId);
            load = null!;

            provider.GetRequiredService<LoadingCatalogRefreshTracker>().MarkRefreshed("native-library-pre-unload-proof");
            var marker = await FindSinglePackageMarkerTypeAsync(provider, "Root.Second");
            Assert.Equal(NativeLibraryFixtureCompiler.ExpectedValue,
                InvokeNativeProbe(marker));

            var context = CaptureFacadeContextWeakReference(
                provider, owner.Lease, marker, packageId: "Root.Second", version: "1.0.0");
            var uses = await InspectGraphUseRecordsAsync(provider.GetRequiredService<IPackageStoreAdmission>(), fixture);
            var use = Assert.Single(uses.Entries);
            var leaseSnapshot = owner.Lease.Snapshot;

            await owner.DisposeAsync();
            owner = null;
            loader.UnloadContextsNotActive(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

            ForceFullCollection();
            Assert.True(IsWeakTargetAlive(context));
            Assert.Equal(NativeLibraryFixtureCompiler.ExpectedValue, InvokeNativeProbe(marker));
            Assert.True(IsWeakTargetAlive(context));
            await AssertUseRemainsLiveAsync(provider, fixture, leaseSnapshot,
                use.Record.UseId, use.Record.SentinelIdentity);

            GC.KeepAlive(marker);
            return new PreloadedNativeLifetimeProof(context, use.Record.UseId, use.Record.SentinelIdentity);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            if (owner is not null)
            {
                try { await owner.DisposeAsync(); }
                catch (Exception cleanupFailure) when (failure is not null)
                {
                    throw new AggregateException("Preloaded native-library lifetime proof and external-owner cleanup failed.",
                        failure, cleanupFailure);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<NativeLifetimeProof> AssertRetainedNativeExportAsync(
        GraphUseFixture fixture,
        ServiceProvider provider,
        string nativePath,
        string nativeDigest)
    {
        var loader = provider.GetRequiredService<PackageLoader>();
        PackageGraphUseLeaseOwner? owner = null;
        Exception? failure = null;
        try
        {
            var oldGraph = fixture.Graphs["member-b"];
            owner = await AcquireGraphUseAsync(provider, oldGraph);
            var load = await provider.GetRequiredService<IScopedPackageLoader>().EnsureGraphLoadedAsync(
                [oldGraph.CreateEnvelope(owner)], [], CancellationToken.None);
            Assert.Empty(load.FailedByPackageId);
            load = null!;

            provider.GetRequiredService<LoadingCatalogRefreshTracker>().MarkRefreshed("native-library-lifetime-proof");
            var retainedMarker = await FindSinglePackageMarkerTypeAsync(provider, "Root.Second");
            Assert.Equal("Root.Second.Marker", retainedMarker.FullName);
            Assert.NotNull(retainedMarker.GetMethod(NativeLibraryFixtureCompiler.MethodName,
                BindingFlags.Public | BindingFlags.Static));

            var contextWeakReference = CaptureFacadeContextWeakReference(
                provider, owner.Lease, retainedMarker, packageId: "Root.Second", version: "1.0.0");
            var leaseSnapshot = owner.Lease.Snapshot;
            Assert.Equal(new[] { "Root.Second", "Shared.Dependency" },
                leaseSnapshot.Nodes.Select(static node => node.Install.PackageId)
                    .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase));

            var initialUses = await InspectGraphUseRecordsAsync(provider.GetRequiredService<IPackageStoreAdmission>(), fixture);
            var initialUse = Assert.Single(initialUses.Entries);
            Assert.Equal(GraphUseRecordOwnershipState.Live, initialUse.OwnershipState);
            Assert.Equal(leaseSnapshot.GraphId, initialUse.Record.GraphSnapshot.GraphId);
            Assert.Equal(leaseSnapshot.GenerationId, initialUse.Record.GraphSnapshot.GenerationId);
            Assert.Equal(leaseSnapshot.Nodes, initialUse.Record.GraphSnapshot.Nodes);
            var useId = initialUse.Record.UseId;
            var sentinelIdentity = initialUse.Record.SentinelIdentity;
            var controlPath = Path.Combine(fixture.RootPath, RootMembershipRegistry.ControlDirectoryName);
            var useRecordPath = Path.Combine(controlPath, $"use-{useId:N}.json");
            var useRecordBytes = await File.ReadAllBytesAsync(useRecordPath);
            Assert.False(await TryAcquireGraphUseSentinelAsync(fixture, useId, sentinelIdentity));

            await owner.DisposeAsync();
            owner = null;
            loader.UnloadContextsNotActive(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

            ForceFullCollection();
            Assert.True(IsWeakTargetAlive(contextWeakReference));
            Assert.True(retainedMarker.IsAssignableTo(typeof(object)));
            await AssertUseRemainsLiveAsync(provider, fixture, leaseSnapshot, useId, sentinelIdentity);

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var generationResult = await provider.GetRequiredService<IReconciliationService>()
                .TriggerAsync(ReconciliationTrigger.Manual("t086-native-library-retire-old-root"), deadline.Token)
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(generationResult.Skipped);
            var committedState = await ReadCoordinatedStateAsync(provider, deadline.Token);
            Assert.Equal("2.0.0", committedState.ActiveVersionById["Root.Second"]);
            Assert.Equal("2.0.0", committedState.LastKnownGoodById["Root.Second"]);
            var verifiedState = PersistedStoreStateGraphVerifier.Verify(committedState);
            var oldRootInstall = leaseSnapshot.Nodes
                .Single(static node => string.Equals(node.Install.PackageId, "Root.Second", StringComparison.OrdinalIgnoreCase))
                .Install;
            Assert.DoesNotContain(verifiedState.ActiveGraphs.Concat(verifiedState.RecoverableGraphs)
                .SelectMany(static graph => graph.Nodes)
                .Select(static node => node.Install), install => install == oldRootInstall);

            var expectedStateBytes = fixture.StatePaths.ToDictionary(static pair => pair.Key,
                static pair => File.ReadAllBytes(pair.Value), StringComparer.Ordinal);
            var expectedInstallEvidence = CaptureFixtureNativeEvidence(fixture);

            // This is deliberately the first call into the packaged native library. The external
            // lease owner is gone and the loader was already asked to retire the old context.
            var nativeResult = InvokeNativeProbe(retainedMarker);
            Assert.Equal(NativeLibraryFixtureCompiler.ExpectedValue, nativeResult);
            Assert.True(IsWeakTargetAlive(contextWeakReference));
            Assert.Equal("Root.Second.Marker", retainedMarker.FullName);
            Assert.Equal(nativeDigest, HashFile(nativePath!));
            Assert.Equal(expectedInstallEvidence.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
                CaptureFixtureNativeEvidence(fixture).OrderBy(static pair => pair.Key, StringComparer.Ordinal));
            foreach (var pair in expectedStateBytes)
                Assert.Equal(pair.Value, await File.ReadAllBytesAsync(fixture.StatePaths[pair.Key]));
            await AssertUseRemainsLiveAsync(provider, fixture, leaseSnapshot, useId, sentinelIdentity);
            Assert.Equal(useRecordBytes, await File.ReadAllBytesAsync(useRecordPath));

            GC.KeepAlive(retainedMarker);
            return new NativeLifetimeProof(contextWeakReference, useId, sentinelIdentity,
                useRecordPath, useRecordBytes, expectedStateBytes, expectedInstallEvidence);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            if (owner is not null)
            {
                try { await owner.DisposeAsync(); }
                catch (Exception cleanupFailure) when (failure is not null)
                {
                    throw new AggregateException("Native-library lifetime proof and external-owner cleanup failed.",
                        failure, cleanupFailure);
                }
            }
        }
    }

    private static async Task CompleteNativeLifetimeTestAsync(
        GraphUseFixture fixture,
        ServiceProvider? provider,
        Exception? primaryFailure)
    {
        var failures = new List<Exception>();
        if (primaryFailure is not null)
            failures.Add(primaryFailure);
        if (provider is not null)
        {
            try
            {
                provider.GetRequiredService<PackageLoader>()
                    .UnloadContextsNotActive(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                // The reconciliation also loaded the newer generation. Drain every use while the
                // passive observer is still running, even after the old sentinel was released.
                await fixture.WaitForGraphUseReleaseAsync(provider.GetRequiredService<IPackageStoreAdmission>());
            }
            catch (Exception exception) { failures.Add(exception); }
            try { await provider.DisposeAsync(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        try { fixture.Dispose(); }
        catch (Exception exception) { failures.Add(exception); }

        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException("Native-library lifetime proof or owned cleanup failed.", failures);
    }

    private sealed record NativeLifetimeProof(
        WeakReference Context,
        Guid UseId,
        PhysicalFileIdentity SentinelIdentity,
        string UseRecordPath,
        byte[] UseRecordBytes,
        IReadOnlyDictionary<string, byte[]> StateBytes,
        IReadOnlyDictionary<string, NativeInstallEvidence> InstallEvidence);

    private sealed record PreloadedNativeLifetimeProof(
        WeakReference Context,
        Guid UseId,
        PhysicalFileIdentity SentinelIdentity);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<Type> FindSinglePackageMarkerTypeAsync(IServiceProvider provider, string packageId)
    {
        var types = await provider.GetRequiredService<IPackageTypeFinder>()
            .FindTypesAsync<object>(packageId, CancellationToken.None);
        return Assert.Single(types);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int InvokeNativeProbe(Type markerType)
    {
        var method = markerType.GetMethod(NativeLibraryFixtureCompiler.MethodName,
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<int>(method!.Invoke(null, [NativeLibraryFixtureCompiler.InputValue]));
    }

    private static async Task AssertUseRemainsLiveAsync(
        IServiceProvider provider,
        GraphUseFixture fixture,
        PackageGraphUseSnapshot expectedGraph,
        Guid useId,
        PhysicalFileIdentity sentinelIdentity)
    {
        var inspection = await InspectGraphUseRecordsAsync(provider.GetRequiredService<IPackageStoreAdmission>(), fixture);
        var entry = Assert.Single(inspection.Entries, candidate => candidate.Record.UseId == useId);
        Assert.Equal(GraphUseRecordOwnershipState.Live, entry.OwnershipState);
        Assert.Equal(sentinelIdentity, entry.Record.SentinelIdentity);
        Assert.Equal(expectedGraph.GraphId, entry.Record.GraphSnapshot.GraphId);
        Assert.Equal(expectedGraph.GenerationId, entry.Record.GraphSnapshot.GenerationId);
        Assert.Equal(expectedGraph.Nodes, entry.Record.GraphSnapshot.Nodes);
        Assert.False(await TryAcquireGraphUseSentinelAsync(fixture, useId, sentinelIdentity));
    }

    private static async Task<StoreStateRecord> ReadCoordinatedStateAsync(
        IServiceProvider provider,
        CancellationToken cancellationToken)
    {
        var admission = provider.GetRequiredService<IPackageStoreAdmission>();
        await using var operation = await admission.AcquireConfiguredRootOperationAsync(
            PackageStoreAdmissionKind.Maintenance, cancellationToken);
        using var borrow = operation.Owner!.Borrow();
        return await Assert.IsAssignableFrom<ICoordinatedStoreRegistry>(provider.GetRequiredService<IStoreRegistry>())
            .ReadCoordinatedStateAsync(borrow, cancellationToken);
    }

    private static string HashFile(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
