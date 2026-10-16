using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Loading;
using Nuplane.Store.Coordination;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Fact]
    public async Task ParticipantAssemblyCatalogResult_RetainsContextAndGraphUseUntilReleased()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = fixture.CreateProvider("member-a", participant: true);

        await AssertFacadeResultLifetimeAsync(
            fixture,
            provider,
            fixture.Graphs["member-a"],
            static services => GetSinglePackageAssemblyAsync(services),
            static assembly =>
            {
                Assert.Equal("Root.First", assembly.GetName().Name);
                Assert.Equal("Root.First.Marker", assembly.GetTypes().Single().FullName);
            });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<Assembly> GetSinglePackageAssemblyAsync(IServiceProvider services)
    {
        var package = await services.GetRequiredService<IPackageAssemblyCatalog>()
            .GetPackagedAssembliesAsync("Root.First", CancellationToken.None);
        return Assert.Single(Assert.IsType<PackageAssemblies>(package).Assemblies);
    }

    [Fact]
    public async Task ParticipantTypeFinderResult_RetainsContextAndGraphUseUntilReleased()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = fixture.CreateProvider("member-a", participant: true);

        await AssertFacadeResultLifetimeAsync(
            fixture,
            provider,
            fixture.Graphs["member-a"],
            static services => FindSinglePackageMarkerTypeAsync(services),
            static type => Assert.Equal("Root.First.Marker", type.FullName));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<Type> FindSinglePackageMarkerTypeAsync(IServiceProvider services)
    {
        var types = await services.GetRequiredService<IPackageTypeFinder>()
            .FindTypesAsync<object>("Root.First", CancellationToken.None);
        return Assert.Single(types);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AssertFacadeResultLifetimeAsync<TResult>(
        GraphUseFixture fixture,
        ServiceProvider provider,
        ResolvedGraphFixture graph,
        Func<IServiceProvider, Task<TResult>> getFacadeResult,
        Action<TResult> assertUsable)
        where TResult : class
    {
        PackageGraphUseLeaseOwner? owner = null;
        TResult? retainedResult = null;
        var sentinelReleaseObserved = false;
        var loader = provider.GetRequiredService<PackageLoader>();
        try
        {
            owner = await AcquireGraphUseAsync(provider, graph);
            var load = await provider.GetRequiredService<IScopedPackageLoader>().EnsureGraphLoadedAsync(
                [graph.CreateEnvelope(owner)], [], CancellationToken.None);
            Assert.Empty(load.FailedByPackageId);
            load = null!;

            provider.GetRequiredService<LoadingCatalogRefreshTracker>().MarkRefreshed("facade-lifetime-proof");
            var stateBefore = fixture.StatePaths.ToDictionary(static pair => pair.Key,
                static pair => Convert.ToHexString(File.ReadAllBytes(pair.Value)), StringComparer.Ordinal);
            var installsBefore = CaptureFixtureNativeEvidence(fixture);

            retainedResult = await getFacadeResult(provider);
            assertUsable(retainedResult);
            var contextWeakReference = CaptureFacadeContextWeakReference(provider, owner.Lease, retainedResult);
            var useInspection = await InspectGraphUseRecordsAsync(
                provider.GetRequiredService<IPackageStoreAdmission>(), fixture);
            var liveRecord = Assert.Single(useInspection.Entries);
            Assert.Equal(GraphUseRecordOwnershipState.Live, liveRecord.OwnershipState);
            var useId = liveRecord.Record.UseId;
            var sentinelIdentity = liveRecord.Record.SentinelIdentity;
            var leaseGraphId = owner.Lease.Snapshot.GraphId;
            var leaseGenerationId = owner.Lease.Snapshot.GenerationId;
            var leaseNodes = owner.Lease.Snapshot.Nodes.ToArray();
            Assert.Equal(leaseGraphId, liveRecord.Record.GraphSnapshot.GraphId);
            Assert.Equal(leaseGenerationId, liveRecord.Record.GraphSnapshot.GenerationId);
            Assert.Equal(leaseNodes, liveRecord.Record.GraphSnapshot.Nodes);
            var controlPath = Path.Combine(fixture.RootPath, RootMembershipRegistry.ControlDirectoryName);
            var recordPath = Path.Combine(controlPath, $"use-{useId:N}.json");
            var recordBytesBeforeRelease = await File.ReadAllBytesAsync(recordPath);

            Assert.False(await TryAcquireGraphUseSentinelAsync(fixture, useId, sentinelIdentity));

            await owner.DisposeAsync();
            owner = null;
            loader.UnloadContextsNotActive(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

            ForceFullCollection();
            assertUsable(retainedResult);
            Assert.True(IsWeakTargetAlive(contextWeakReference));

            var stillLive = await InspectGraphUseRecordsAsync(
                provider.GetRequiredService<IPackageStoreAdmission>(), fixture);
            var stillLiveRecord = Assert.Single(stillLive.Entries);
            Assert.Equal(useId, stillLiveRecord.Record.UseId);
            Assert.Equal(sentinelIdentity, stillLiveRecord.Record.SentinelIdentity);
            Assert.Equal(GraphUseRecordOwnershipState.Live, stillLiveRecord.OwnershipState);
            Assert.False(await TryAcquireGraphUseSentinelAsync(fixture, useId, sentinelIdentity));
            GC.KeepAlive(retainedResult);

            retainedResult = null;
            await WaitForWeakTargetDeathAsync(contextWeakReference);

            // Probe the same native sentinel directly: no Maintenance admission/recovery operation
            // participates in releasing the context-owned graph-use lease.
            await WaitForGraphUseSentinelReleaseAsync(fixture, useId, sentinelIdentity);
            sentinelReleaseObserved = true;

            var staleInspection = await InspectGraphUseRecordsAsync(
                provider.GetRequiredService<IPackageStoreAdmission>(), fixture);
            var staleRecord = Assert.Single(staleInspection.Entries);
            Assert.Equal(useId, staleRecord.Record.UseId);
            Assert.Equal(sentinelIdentity, staleRecord.Record.SentinelIdentity);
            Assert.Equal(GraphUseRecordOwnershipState.Stale, staleRecord.OwnershipState);
            Assert.Equal(recordBytesBeforeRelease, await File.ReadAllBytesAsync(recordPath));

            foreach (var state in stateBefore)
            {
                Assert.Equal(state.Value,
                    Convert.ToHexString(await File.ReadAllBytesAsync(fixture.StatePaths[state.Key])));
            }
            Assert.Equal(installsBefore.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
                CaptureFixtureNativeEvidence(fixture).OrderBy(static pair => pair.Key, StringComparer.Ordinal));
        }
        finally
        {
            retainedResult = null;
            if (owner is not null)
                await owner.DisposeAsync();
            loader.UnloadContextsNotActive(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            if (!sentinelReleaseObserved)
                await fixture.WaitForGraphUseReleaseAsync(provider.GetRequiredService<IPackageStoreAdmission>());
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CaptureFacadeContextWeakReference(
        IServiceProvider provider,
        PackageGraphUseLease lease,
        object facadeResult,
        string packageId = "Root.First",
        string version = "1.0.0")
    {
        Assert.True(provider.GetRequiredService<PackageLoader>()
            .TryGetContext(packageId, version, out var handle));
        var context = Assert.IsAssignableFrom<AssemblyLoadContext>(handle!.Context);
        Assert.Same(lease, Assert.IsAssignableFrom<IGraphUseLeaseBoundLoadContext>(context).GraphUseLease);

        var resultAssembly = facadeResult switch
        {
            Assembly assembly => assembly,
            Type type => type.Assembly,
            _ => throw new Xunit.Sdk.XunitException($"Unexpected facade result type {facadeResult.GetType()}.")
        };
        Assert.Same(context, AssemblyLoadContext.GetLoadContext(resultAssembly));
        // Short weak references clear before finalization; track resurrection so this probe observes
        // completed collectible-context unload. The native-file removal assertion is separate.
        return new WeakReference(context, trackResurrection: true);
    }

    private static async Task WaitForWeakTargetDeathAsync(WeakReference weakReference)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            ForceFullCollection();
            if (!IsWeakTargetAlive(weakReference))
                return;
            await Task.Delay(100);
        }

        Assert.False(IsWeakTargetAlive(weakReference), "The facade result kept its collectible load context alive after release.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceFullCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static async Task<bool> TryAcquireGraphUseSentinelAsync(
        GraphUseFixture fixture,
        Guid useId,
        PhysicalFileIdentity expectedSentinelIdentity)
    {
        using var control = fixture.Files.OpenDirectoryChildNoFollow(
            fixture.Root, RootMembershipRegistry.ControlDirectoryName);
        using var sentinel = fixture.Files.OpenFileChildNoFollow(
            control, $"use-{useId:N}.sentinel", FileAccess.ReadWrite);
        Assert.Equal(expectedSentinelIdentity, fixture.Files.InspectHandle(sentinel).Identity);
        var acquired = await fixture.Files.TryAcquireExclusiveLock(sentinel);
        if (acquired is null)
            return false;

        await acquired.DisposeAsync();
        return true;
    }

    private static async Task WaitForGraphUseSentinelReleaseAsync(
        GraphUseFixture fixture,
        Guid useId,
        PhysicalFileIdentity expectedSentinelIdentity)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (await TryAcquireGraphUseSentinelAsync(fixture, useId, expectedSentinelIdentity))
                return;
            await Task.Delay(100);
        }

        Assert.True(await TryAcquireGraphUseSentinelAsync(fixture, useId, expectedSentinelIdentity),
            $"Graph-use sentinel for use {useId:N} remained locked after its context was collected.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsWeakTargetAlive(WeakReference weakReference) => weakReference.IsAlive;
}
