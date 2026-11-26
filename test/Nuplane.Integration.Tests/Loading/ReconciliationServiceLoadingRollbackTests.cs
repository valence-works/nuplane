using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Nuplane.Abstractions;
using Nuplane.Integration.Tests.Restore;
using Nuplane.Loading;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Loading;

/// <summary>Exercises ordinary file-backed reconciliation with the real package loader.</summary>
public sealed class ReconciliationServiceLoadingRollbackTests : IDisposable
{
    private const string InitialVersion = "1.0.0";
    private const string HealthyUpdateVersion = "2.0.0";
    private const string RefusedVersion = "3.0.0";
    private const string RefusalReason = "The host cannot activate this fixture version.";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("nuplane-loading-rollback-");
    private readonly string _packageId = $"Rollback.Fixture.N{Guid.NewGuid():N}";

    private string FeedPath => Path.Combine(_root.FullName, "feed");
    private string StatePath => Path.Combine(_root.FullName, "store-state.json");

    [Fact]
    public async Task TriggerAsync_RefusedUpdate_PreservesActiveGraphUntilSuccessfulRemoval()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = timeout.Token;
        Publish(InitialVersion);
        using var host = BuildHost();
        await host.StartAsync(cancellationToken);

        try
        {
            await AssertLoadedVersionAsync(host, InitialVersion, cancellationToken);

            Publish(HealthyUpdateVersion);
            var healthyUpdate = await TriggerAsync(host, cancellationToken);
            Assert.False(healthyUpdate.IsDegraded);
            Assert.Equal(HealthyUpdateVersion, Assert.Single(healthyUpdate.ChangeSet.Updated).Version);
            var previousGraph = await AssertPersistedVersionAsync(HealthyUpdateVersion, cancellationToken);
            await AssertLoadedVersionAsync(host, HealthyUpdateVersion, cancellationToken);

            Publish(RefusedVersion);
            var refusal = await TriggerAsync(host, cancellationToken);

            Assert.True(refusal.IsDegraded);
            Assert.Equal(_packageId, Assert.Single(refusal.FailedPackages));
            var afterRefusal = await NuplaneStore.ReadStateAsync(StatePath, cancellationToken);
            var failure = afterRefusal.LastFailureById[_packageId];
            Assert.Equal("load", failure.Stage);
            Assert.Contains(RefusalReason, failure.Message, StringComparison.Ordinal);
            var retainedGraph = AssertPersistedVersion(afterRefusal, HealthyUpdateVersion);
            Assert.Equal(previousGraph.GraphId, retainedGraph.GraphId);
            Assert.Equal(previousGraph.GenerationId, retainedGraph.GenerationId);
            await AssertLoadedVersionAsync(host, HealthyUpdateVersion, cancellationToken);

            foreach (var archive in Directory.EnumerateFiles(FeedPath, "*.nupkg"))
            {
                File.Delete(archive);
            }

            var removal = await TriggerAsync(host, cancellationToken);

            Assert.False(removal.IsDegraded);
            Assert.Equal(_packageId, Assert.Single(removal.ChangeSet.Removed));
            var removedState = await NuplaneStore.ReadStateAsync(StatePath, cancellationToken);
            Assert.Empty(removedState.ActiveVersionById);
            Assert.DoesNotContain(removedState.ActiveGraphsByIdNormalized.Values,
                graph => graph.Status == GraphActivationStatus.Active);
            var assemblies = host.Services.GetRequiredService<IPackageAssemblyCatalog>();
            Assert.Null(await assemblies.GetPackagedAssembliesAsync(_packageId, cancellationToken));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartAsync_RefusedDesiredUpdate_RecoversPreviousGraphFromLastKnownGood()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = timeout.Token;
        Publish(HealthyUpdateVersion);

        GraphActivationRecord previousGraph;
        using (var host = BuildHost())
        {
            await host.StartAsync(cancellationToken);
            try
            {
                previousGraph = await AssertPersistedVersionAsync(HealthyUpdateVersion, cancellationToken);
                await AssertLoadedVersionAsync(host, HealthyUpdateVersion, cancellationToken);
            }
            finally
            {
                await host.StopAsync(CancellationToken.None);
            }
        }

        Publish(RefusedVersion);
        using var restartedHost = BuildHost();
        await restartedHost.StartAsync(cancellationToken);

        try
        {
            var gate = restartedHost.Services.GetRequiredService<RejectFixtureUpdate>();
            Assert.Contains(RefusedVersion, gate.AttemptedVersions);
            var recoveredGraph = await AssertPersistedVersionAsync(HealthyUpdateVersion, cancellationToken);
            Assert.Equal(previousGraph.GraphId, recoveredGraph.GraphId);
            Assert.Equal(previousGraph.GenerationId, recoveredGraph.GenerationId);
            await AssertLoadedVersionAsync(restartedHost, HealthyUpdateVersion, cancellationToken);
        }
        finally
        {
            await restartedHost.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TriggerAsync_RefusedGraph_PreservesDependencySessionAndPublishesIndependentUpdate()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = timeout.Token;
        var dependency = HostFreeRestoreTestSupport.WriteNupkgForId(FeedPath, $"{_packageId}.Dependency", InitialVersion);
        var independentId = $"{_packageId}.Independent";
        Publish(InitialVersion, dependency);
        HostFreeRestoreTestSupport.WriteNupkgForId(FeedPath, independentId, InitialVersion);
        using var host = BuildHost(additionalRoot: independentId);
        await host.StartAsync(cancellationToken);

        try
        {
            var before = await NuplaneStore.ReadStateAsync(StatePath, cancellationToken);
            var previousGraph = AssertPersistedVersion(before, InitialVersion);
            var previousDependency = before.ActivePackageDescriptorsByIdNormalized[dependency.PackageId];
            Assert.Equal(previousGraph.GraphId, previousDependency.GraphId);
            Assert.Equal(previousGraph.GenerationId, previousDependency.GraphGenerationId);
            var previousRootAssembly = await AssertLoadedVersionAsync(host, InitialVersion, cancellationToken);
            var loader = host.Services.GetRequiredService<PackageLoader>();
            Assert.True(loader.TryGetContext(dependency.PackageId, InitialVersion, out var previousDependencyContext));
            Assert.NotNull(previousDependencyContext);

            Publish(RefusedVersion, dependency);
            HostFreeRestoreTestSupport.WriteNupkgForId(FeedPath, independentId, HealthyUpdateVersion);
            var result = await TriggerAsync(host, cancellationToken);

            Assert.True(result.IsDegraded);
            Assert.Contains(_packageId, result.FailedPackages);
            Assert.DoesNotContain(independentId, result.FailedPackages);
            var after = await NuplaneStore.ReadStateAsync(StatePath, cancellationToken);
            var retainedGraph = AssertPersistedVersion(after, InitialVersion);
            Assert.Equal(previousGraph.GraphId, retainedGraph.GraphId);
            Assert.Equal(previousGraph.GenerationId, retainedGraph.GenerationId);
            Assert.Equal(InitialVersion, after.LastKnownGoodById[dependency.PackageId]);
            Assert.Equal(InitialVersion, after.ActiveVersionById[dependency.PackageId]);
            var retainedDependency = after.ActivePackageDescriptorsByIdNormalized[dependency.PackageId];
            Assert.Equal(previousDependency.InstallPath, retainedDependency.InstallPath);
            Assert.Equal(previousDependency.GraphId, retainedDependency.GraphId);
            Assert.Equal(previousDependency.GraphGenerationId, retainedDependency.GraphGenerationId);
            Assert.Same(previousRootAssembly, await AssertLoadedVersionAsync(host, InitialVersion, cancellationToken));
            Assert.True(loader.TryGetContext(dependency.PackageId, InitialVersion, out var retainedDependencyContext));
            Assert.NotNull(retainedDependencyContext);
            Assert.Same(previousDependencyContext.Context, retainedDependencyContext.Context);

            AssertPersistedVersion(after, HealthyUpdateVersion, independentId);
            await AssertLoadedVersionAsync(host, HealthyUpdateVersion, cancellationToken, independentId);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TriggerAsync_ActivationPersistenceFails_PreservesPreviousSessionAndCanRetry()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = timeout.Token;
        Publish(InitialVersion);
        var serializer = new FailingActivationSerializer(_packageId);
        using var host = BuildHost(serializer: serializer);
        await host.StartAsync(cancellationToken);

        try
        {
            var previousGraph = await AssertPersistedVersionAsync(InitialVersion, cancellationToken);
            var previousAssembly = await AssertLoadedVersionAsync(host, InitialVersion, cancellationToken);

            Publish(HealthyUpdateVersion);
            serializer.FailNextActivation = true;
            var error = await Assert.ThrowsAsync<IOException>(() => TriggerAsync(host, cancellationToken));

            Assert.Equal(FailingActivationSerializer.FailureMessage, error.Message);
            var retainedGraph = await AssertPersistedVersionAsync(InitialVersion, cancellationToken);
            Assert.Equal(previousGraph.GenerationId, retainedGraph.GenerationId);
            Assert.Same(previousAssembly, await AssertLoadedVersionAsync(host, InitialVersion, cancellationToken));

            var retry = await TriggerAsync(host, cancellationToken);
            Assert.False(retry.IsDegraded);
            await AssertPersistedVersionAsync(HealthyUpdateVersion, cancellationToken);
            Assert.NotSame(previousAssembly, await AssertLoadedVersionAsync(host, HealthyUpdateVersion, cancellationToken));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TriggerAsync_HostIntegratedReplacement_ResolvesCurrentAssemblyByNameAfterRefusedUpdate()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = timeout.Token;
        Publish(InitialVersion);
        using var host = BuildHost(loadMode: PackageLoadMode.HostIntegrated);
        await host.StartAsync(cancellationToken);

        try
        {
            var initialAssembly = await AssertLoadedVersionAsync(host, InitialVersion, cancellationToken);

            Publish(HealthyUpdateVersion);
            var healthyUpdate = await TriggerAsync(host, cancellationToken);

            Assert.False(healthyUpdate.IsDegraded);
            await AssertPersistedVersionAsync(HealthyUpdateVersion, cancellationToken);
            var updatedAssembly = await AssertLoadedVersionAsync(host, HealthyUpdateVersion, cancellationToken);
            Assert.Equal(initialAssembly.FullName, updatedAssembly.FullName);
            Assert.NotSame(initialAssembly, updatedAssembly);
            var name = new AssemblyName(updatedAssembly.FullName!);
            var resolution = host.Services.GetRequiredService<HostIntegratedAssemblyResolutionCatalog>();
            Assert.True(resolution.TryResolve(name, out var beforeRefusal, out _));
            Assert.Same(updatedAssembly, beforeRefusal);

            Publish(RefusedVersion);
            var refusedUpdate = await TriggerAsync(host, cancellationToken);

            Assert.True(refusedUpdate.IsDegraded);
            await AssertPersistedVersionAsync(HealthyUpdateVersion, cancellationToken);
            Assert.Same(updatedAssembly, await AssertLoadedVersionAsync(host, HealthyUpdateVersion, cancellationToken));
            Assert.True(resolution.TryResolve(name, out var afterRefusal, out _));
            Assert.Same(updatedAssembly, afterRefusal);
            Assert.Same(updatedAssembly, Assembly.Load(name));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Windows can retain loaded assembly file handles until their contexts are collected.
        }
    }

    private IHost BuildHost(
        string? additionalRoot = null,
        IStoreStateSerializer? serializer = null,
        PackageLoadMode loadMode = PackageLoadMode.Collectible)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        var settings = new Dictionary<string, string?>
        {
            ["Setup:Feeds:local:DirectoryPath"] = FeedPath,
            ["Setup:Feeds:local:Trust"] = "Trusted",
            ["Setup:Feeds:local:IncludePatterns:0"] = _packageId,
            ["Setup:Feeds:local:Directory:Watch"] = "false",
            ["StoreRegistry:StateFilePath"] = StatePath,
            ["FeedResolution:PackageInstallRoot"] = Path.Combine(_root.FullName, "installs"),
            ["Reconciliation:StartupFailurePolicy"] = "UseLastKnownGood",
            ["LockFile:Path"] = Path.Combine(_root.FullName, "nuplane.lock.json")
        };
        if (additionalRoot is not null)
            settings["Setup:Feeds:local:IncludePatterns:1"] = additionalRoot;

        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddLogging();
        builder.Services.AddNuplane(builder.Configuration, nuplane => nuplane.AutoloadPackages(loading =>
        {
            loading.WithDefaultLoadMode(loadMode);
            loading.AddActivationGate<RejectFixtureUpdate>();
        }));
        if (serializer is not null)
            builder.Services.Replace(ServiceDescriptor.Singleton(serializer));

        return builder.Build();
    }

    private void Publish(string version, PackageFixture? dependency = null) =>
        HostFreeRestoreTestSupport.WriteNupkgForId(FeedPath, _packageId, version, dependency: dependency);

    private static Task<ReconciliationRunResult> TriggerAsync(IHost host, CancellationToken cancellationToken) =>
        host.Services.GetRequiredService<IReconciliationService>()
            .TriggerAsync(ReconciliationTrigger.Manual(), cancellationToken);

    private async Task<GraphActivationRecord> AssertPersistedVersionAsync(
        string version, CancellationToken cancellationToken) =>
        AssertPersistedVersion(await NuplaneStore.ReadStateAsync(StatePath, cancellationToken), version);

    private GraphActivationRecord AssertPersistedVersion(StoreStateRecord state, string version, string? packageId = null)
    {
        packageId ??= _packageId;
        Assert.Equal(version, state.ActiveVersionById[packageId]);
        Assert.Equal(version, state.LastKnownGoodById[packageId]);
        var active = state.ActivePackageDescriptorsByIdNormalized[packageId];
        Assert.Equal(version, active.Version);
        var graph = Assert.Single(state.ActiveGraphsByIdNormalized.Values,
            graph => graph.Status == GraphActivationStatus.Active && graph.RootPackageIds.Contains(packageId));
        Assert.Equal(active.GraphId, graph.GraphId);
        Assert.Equal(active.GraphGenerationId, graph.GenerationId);
        Assert.Equal(version, graph.NodeVersionsByPackageId![packageId]);
        return graph;
    }

    private async Task<Assembly> AssertLoadedVersionAsync(
        IHost host, string version, CancellationToken cancellationToken, string? packageId = null)
    {
        var catalog = host.Services.GetRequiredService<IPackageAssemblyCatalog>();
        var package = await catalog.GetPackagedAssembliesAsync(packageId ?? _packageId, cancellationToken);
        Assert.NotNull(package);
        Assert.Equal(version, package.Version);
        return Assert.Single(package.Assemblies);
    }

    private sealed class FailingActivationSerializer(string packageId) : IStoreStateSerializer
    {
        internal const string FailureMessage = "The test refused the activation state write.";
        private readonly StoreStateSerializer _inner = new();

        public bool FailNextActivation { get; set; }

        public Task<StoreStateRecord> LoadAsync(string stateFilePath, CancellationToken cancellationToken) =>
            _inner.LoadAsync(stateFilePath, cancellationToken);

        public Task SaveAsync(string stateFilePath, StoreStateRecord state, CancellationToken cancellationToken)
        {
            if (FailNextActivation &&
                state.ActiveVersionById.TryGetValue(packageId, out var version) &&
                version == HealthyUpdateVersion)
            {
                FailNextActivation = false;
                throw new IOException(FailureMessage);
            }

            return _inner.SaveAsync(stateFilePath, state, cancellationToken);
        }
    }

    internal sealed class RejectFixtureUpdate : IPackageActivationGate
    {
        public List<string> AttemptedVersions { get; } = [];

        public ValueTask<PackageActivationGateResult> EvaluateAsync(
            PackageActivationContext context,
            CancellationToken cancellationToken)
        {
            AttemptedVersions.AddRange(context.Packages.Select(static package => package.Version));
            return new(context.Packages.Any(static package => package.Version == RefusedVersion)
                ? PackageActivationGateResult.Block(RefusalReason)
                : PackageActivationGateResult.Allow);
        }
    }
}
