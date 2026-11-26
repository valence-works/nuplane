using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    private IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Setup:Feeds:local:DirectoryPath"] = FeedPath,
            ["Setup:Feeds:local:Trust"] = "Trusted",
            ["Setup:Feeds:local:IncludeAll"] = "true",
            ["Setup:Feeds:local:Directory:Watch"] = "false",
            ["StoreRegistry:StateFilePath"] = StatePath,
            ["FeedResolution:PackageInstallRoot"] = Path.Combine(_root.FullName, "installs"),
            ["Reconciliation:StartupFailurePolicy"] = "UseLastKnownGood",
            ["LockFile:Path"] = Path.Combine(_root.FullName, "nuplane.lock.json")
        });
        builder.Services.AddLogging();
        builder.Services.AddNuplane(builder.Configuration, nuplane =>
            nuplane.AutoloadPackages(loading => loading.AddActivationGate<RejectFixtureUpdate>()));

        return builder.Build();
    }

    private void Publish(string version) =>
        HostFreeRestoreTestSupport.WriteNupkgForId(FeedPath, _packageId, version);

    private static Task<ReconciliationRunResult> TriggerAsync(IHost host, CancellationToken cancellationToken) =>
        host.Services.GetRequiredService<IReconciliationService>()
            .TriggerAsync(ReconciliationTrigger.Manual(), cancellationToken);

    private async Task<GraphActivationRecord> AssertPersistedVersionAsync(
        string version, CancellationToken cancellationToken) =>
        AssertPersistedVersion(await NuplaneStore.ReadStateAsync(StatePath, cancellationToken), version);

    private GraphActivationRecord AssertPersistedVersion(StoreStateRecord state, string version)
    {
        Assert.Equal(version, state.ActiveVersionById[_packageId]);
        Assert.Equal(version, state.LastKnownGoodById[_packageId]);
        var active = state.ActivePackageDescriptorsByIdNormalized[_packageId];
        Assert.Equal(version, active.Version);
        var graph = Assert.Single(state.ActiveGraphsByIdNormalized.Values,
            graph => graph.Status == GraphActivationStatus.Active);
        Assert.Equal(active.GraphId, graph.GraphId);
        Assert.Equal(active.GraphGenerationId, graph.GenerationId);
        Assert.Equal(version, graph.NodeVersionsByPackageId![_packageId]);
        return graph;
    }

    private async Task AssertLoadedVersionAsync(IHost host, string version, CancellationToken cancellationToken)
    {
        var catalog = host.Services.GetRequiredService<IPackageAssemblyCatalog>();
        var package = await catalog.GetPackagedAssembliesAsync(_packageId, cancellationToken);
        Assert.NotNull(package);
        Assert.Equal(version, package.Version);
        Assert.NotEmpty(package.Assemblies);
    }

    internal sealed class RejectFixtureUpdate : IPackageActivationGate
    {
        public List<string> AttemptedVersions { get; } = [];

        public ValueTask<PackageActivationGateResult> EvaluateAsync(
            PackageActivationContext context,
            CancellationToken cancellationToken)
        {
            var package = Assert.Single(context.Packages);
            AttemptedVersions.Add(package.Version);
            return new(package.Version == RefusedVersion
                ? PackageActivationGateResult.Block(RefusalReason)
                : PackageActivationGateResult.Allow);
        }
    }
}
