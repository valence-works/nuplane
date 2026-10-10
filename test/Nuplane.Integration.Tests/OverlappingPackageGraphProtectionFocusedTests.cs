using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds.Configuration;
using Nuplane.Loading;
using Nuplane.Loading.Hosting.Builder;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Fact]
    public async Task InvalidExactPackagePath_IsRejectedBeforeMetadataRead_AndClosesOwner()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = fixture.CreateProvider("member-a");

        var graph = fixture.Graphs["member-a"];
        PackageGraphUseLeaseOwner? owner = null;
        MetadataReadBarrier? metadataRead = null;
        try
        {
            var acquiredOwner = await AcquireGraphUseAsync(provider, graph);
            owner = acquiredOwner;
            metadataRead = fixture.Files.BlockNextMetadataRead();
            var changedPackages = graph.Packages.Select(package =>
                package.Id == "Root.First"
                    ? package with { InstallPath = Path.Combine(fixture.RootPath, "outside-the-leased-graph") }
                    : package).ToArray();

            var refused = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
                provider.GetRequiredService<IScopedPackageLoader>().EnsureGraphLoadedAsync(
                    [graph.CreateEnvelope(acquiredOwner, changedPackages)], [], CancellationToken.None));

            Assert.Equal(PackageStoreAdmissionReason.StateMismatch, refused.Reason);
            Assert.False(metadataRead.HasStarted);
        }
        finally
        {
            metadataRead?.Release();
            await UnloadAndWaitForReleaseAsync(provider, fixture, owner);
        }
    }

    [Fact]
    public async Task CallerListMutationDuringActivationGate_DoesNotChangeValidatedGraph()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        var graph = fixture.Graphs["member-a"];
        var graphs = new List<ScopedResolvedPackageGraph>();
        var gate = new PathIndependentCallbackGate(() =>
        {
            var current = Assert.Single(graphs);
            var changedPackages = graph.Packages.Select(package =>
                package.Id == "Root.First"
                    ? package with { InstallPath = Path.Combine(fixture.RootPath, "outside-the-leased-graph") }
                    : package).ToArray();
            graphs[0] = graph.CreateEnvelope(current.LeaseOwner, changedPackages);
        });
        await using var provider = fixture.CreateProvider("member-a", gate);

        PackageGraphUseLeaseOwner? owner = null;
        try
        {
            owner = await AcquireGraphUseAsync(provider, graph);
            graphs.Add(graph.CreateEnvelope(owner));

            var loaded = await provider.GetRequiredService<IScopedPackageLoader>().EnsureGraphLoadedAsync(
                graphs, [], CancellationToken.None);

            Assert.Empty(loaded.FailedByPackageId);
            Assert.Contains(loaded.Loaded, session => session.PackageId == "Root.First" && session.Version == "1.0.0");
            Assert.Equal(1, gate.CallCount);
            Assert.Equal(Path.Combine(fixture.RootPath, "outside-the-leased-graph"),
                Assert.Single(graphs[0].Packages, package => package.Id == "Root.First").InstallPath);
            AssertGraphLoaded(provider, graph, "Root.First", "Shared.Dependency");
        }
        finally
        {
            await UnloadAndWaitForReleaseAsync(provider, fixture, owner);
        }
    }

    [Fact]
    public async Task FailureAfterGraphContextCreation_ReleasesTransferredOwnerAfterUnload()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = fixture.CreateProvider("member-a");

        var graph = fixture.Graphs["member-a"];
        PackageGraphUseLeaseOwner? owner = null;
        Assembly? retainedAssembly = null;
        AssemblyLoadContext? retainedContext = null;
        var graphKey = PackageLoader.BuildGraphKey(graph.Packages);
        void CapturePackageAssembly(object? _, AssemblyLoadEventArgs args)
        {
            if (!string.Equals(args.LoadedAssembly.GetName().Name, "Root.First", StringComparison.Ordinal))
                return;
            var context = AssemblyLoadContext.GetLoadContext(args.LoadedAssembly);
            if (string.Equals(context?.Name, graphKey, StringComparison.Ordinal))
            {
                retainedAssembly = args.LoadedAssembly;
                retainedContext = context;
            }
        }

        AppDomain.CurrentDomain.AssemblyLoad += CapturePackageAssembly;
        try
        {
            owner = await AcquireGraphUseAsync(provider, graph);
            var sharedDependency = graph.Packages.Single(package => package.Id == "Shared.Dependency");
            await File.WriteAllBytesAsync(Path.Combine(sharedDependency.InstallPath, "Shared.Dependency.dll"), [0, 1, 2, 3]);

            var result = await provider.GetRequiredService<IScopedPackageLoader>().EnsureGraphLoadedAsync(
                [graph.CreateEnvelope(owner)], [], CancellationToken.None);

            Assert.Empty(result.Loaded);
            Assert.Contains("Root.First", result.FailedByPackageId.Keys);
            Assert.Contains("Shared.Dependency", result.FailedByPackageId.Keys);

            AssertCapturedGraphAssembly(retainedAssembly, retainedContext, graphKey);
            await owner.DisposeAsync();
            var liveRecords = await InspectGraphUseRecordsAsync(
                provider.GetRequiredService<IPackageStoreAdmission>(), fixture);
            var liveRecord = Assert.Single(liveRecords.Entries);
            Assert.Equal(GraphUseRecordOwnershipState.Live, liveRecord.OwnershipState);
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyLoad -= CapturePackageAssembly;
            retainedAssembly = null;
            retainedContext = null;
            await UnloadAndWaitForReleaseAsync(provider, fixture, owner);
        }
    }

    [Fact]
    public async Task UnknownActivationGate_IsRefusedBeforeCallbackOrMetadataRead()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        var gate = new UnknownCallbackGate();
        await using var provider = CreateProvider(fixture, "member-a", services =>
            services.AddSingleton<IPackageActivationGate>(gate));

        await AssertUnknownParticipantRefusedBeforeReadAsync(fixture, provider, gate);
    }

    [Fact]
    public async Task UnknownLoadModeAdvisor_IsRefusedBeforeCallbackOrMetadataRead()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        var advisor = new UnknownCallbackAdvisor();
        await using var provider = CreateProvider(fixture, "member-a", services =>
            services.AddSingleton<IPackageLoadModeAdvisor>(advisor));

        await AssertUnknownParticipantRefusedBeforeReadAsync(fixture, provider, advisor);
    }

    private static async Task AssertUnknownParticipantRefusedBeforeReadAsync(
        GraphUseFixture fixture,
        ServiceProvider provider,
        ICallbackCounter participant)
    {
        var graph = fixture.Graphs["member-a"];
        PackageGraphUseLeaseOwner? owner = null;
        MetadataReadBarrier? metadataRead = null;
        Task<PackageLoadResult>? loading = null;
        try
        {
            var acquiredOwner = await AcquireGraphUseAsync(provider, graph);
            owner = acquiredOwner;
            metadataRead = fixture.Files.BlockNextMetadataRead();
            var loadTask = Task.Run(() => provider.GetRequiredService<IScopedPackageLoader>().EnsureGraphLoadedAsync(
                [graph.CreateEnvelope(acquiredOwner)], [], CancellationToken.None));
            loading = loadTask;

            var refused = await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
                await loadTask.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, refused.Reason);
            Assert.Equal(0, participant.CallCount);
            Assert.False(metadataRead.HasStarted);
        }
        finally
        {
            metadataRead?.Release();
            if (loading is not null)
            {
                try { await loading; }
                catch { }
            }
            await UnloadAndWaitForReleaseAsync(provider, fixture, owner);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertCapturedGraphAssembly(
        Assembly? assembly,
        AssemblyLoadContext? context,
        string graphKey)
    {
        var loadedAssembly = Assert.IsAssignableFrom<Assembly>(assembly);
        var loadedContext = Assert.IsAssignableFrom<AssemblyLoadContext>(context);
        Assert.Equal("Root.First", loadedAssembly.GetName().Name);
        Assert.Equal(graphKey, loadedContext.Name);
    }

    private static ServiceProvider CreateProvider(
        GraphUseFixture fixture,
        string memberId,
        Action<IServiceCollection> registerParticipant)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(nuplane =>
        {
            nuplane.WithStateFile(fixture.StatePaths[memberId]);
            nuplane.AutoloadPackages(loading => loading.WithDefaultLoadMode(PackageLoadMode.Collectible));
        });
        services.Configure<FeedResolutionOptions>(options => options.PackageInstallRoot = fixture.RootPath);
        registerParticipant(services);
        services.RemoveAll<IPhysicalStoreFileSystem>();
        services.AddSingleton<IPhysicalStoreFileSystem>(fixture.Files);
        return services.BuildServiceProvider();
    }

    private static async Task UnloadAndWaitForReleaseAsync(
        ServiceProvider provider,
        GraphUseFixture fixture,
        PackageGraphUseLeaseOwner? owner)
    {
        if (owner is not null)
            await owner.DisposeAsync();
        provider.GetRequiredService<PackageLoader>().UnloadContextsNotActive(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        await fixture.WaitForGraphUseReleaseAsync(provider.GetRequiredService<IPackageStoreAdmission>());
    }

    private interface ICallbackCounter
    {
        int CallCount { get; }
    }

    private abstract class CallbackActivationGate(Action callback) : IPackageActivationGate, ICallbackCounter
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<PackageActivationGateResult> EvaluateAsync(
            PackageActivationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            callback();
            return ValueTask.FromResult(PackageActivationGateResult.Allow);
        }
    }

    private sealed class PathIndependentCallbackGate(Action callback)
        : CallbackActivationGate(callback), IPackagePathIndependentActivationGate
    {
    }

    private sealed class UnknownCallbackGate() : CallbackActivationGate(static () => { })
    {
    }

    private sealed class UnknownCallbackAdvisor : IPackageLoadModeAdvisor, ICallbackCounter
    {
        private int _callCount;

        public string Name => "unknown-test-advisor";
        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<IReadOnlyList<LoadModeAdvisorResult>> EvaluateAsync(
            LoadModeAdvisorContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult<IReadOnlyList<LoadModeAdvisorResult>>([]);
        }
    }
}
