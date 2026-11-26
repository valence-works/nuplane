using System.Runtime.Loader;
using Microsoft.Extensions.Logging.Abstractions;
using Nuplane.Abstractions;
using Nuplane.Loading.Tests.Fixtures;

namespace Nuplane.Loading.Tests;

public sealed class PackageGraphLifetimeTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("nuplane-graph-lifetime-");

    [Fact]
    public void UnloadContextsNotActive_WhenGraphStillHasActiveMember_KeepsContextUntilLastMemberRetires()
    {
        var selector = new PackageAssemblyAssetSelector();
        var logger = NullLogger<PackageLoader>.Instance;
        var lifetime = new PackageGraphLifetime(selector, new(selector, new(), logger), new(), logger, requiresGraphUseLease: false);
        var root = PackageMetadataTestSupport.Package("root", "1.0.0",
            PackageMetadataTestSupport.CreateInstallDir(_temp, "root", typeof(FixtureMarker).Assembly));
        var dependency = PackageMetadataTestSupport.Package("dependency", "3.0.0",
            PackageMetadataTestSupport.CreateInstallDir(_temp, "dependency", typeof(PackageGraphLifetimeTests).Assembly));
        ResolvedPackage[] graph = [root, dependency];
        var graphKey = PackageProjection.BuildGraphKey(graph);
        var selections = graph.Select(package =>
            new PackageLoadModeSelection(package.Id, package.Version, PackageLoadMode.Collectible, "test", graphKey)).ToArray();
        var projections = graph.ToDictionary(package => PackageProjection.BuildKey(package.Id, package.Version),
            package => new PackageProjection(PackageProjection.BuildKey(package.Id, package.Version), package.InstallPath, null, null));
        var loaded = new List<PackageLoadSession>();
        var failed = new Dictionary<string, string>();

        lock (lifetime.SyncRoot)
            lifetime.EnsureGraphLoaded(graph, selections, [], loaded, failed, projections,
                new Dictionary<string, IReadOnlyList<LoadModeDecisionDiagnostic>>());

        Assert.Empty(failed);
        Assert.True(lifetime.TryGetContext(root.Id, root.Version, out var rootHandle));
        Assert.True(lifetime.TryGetContext(dependency.Id, dependency.Version, out var dependencyHandle));
        var context = Assert.IsAssignableFrom<AssemblyLoadContext>(rootHandle!.Context);
        Assert.Same(context, dependencyHandle!.Context);
        var unloadRequested = false;
        context.Unloading += _ => unloadRequested = true;
        try
        {
            Assert.Equal("root@1.0.0",
                Assert.Single(lifetime.UnloadContextsNotActive(new Dictionary<string, string> { [dependency.Id] = dependency.Version })));
            Assert.True(lifetime.TryGetContext(dependency.Id, dependency.Version, out var retainedHandle));
            Assert.Same(context, retainedHandle!.Context);
            Assert.False(unloadRequested);
            lock (lifetime.SyncRoot)
            {
                Assert.False(lifetime.TryGetExactGraphSessions(graph, graphKey, PackageLoadMode.Collectible,
                    false, projections, out _));
            }

            Assert.Equal("dependency@3.0.0", Assert.Single(lifetime.UnloadContextsNotActive(new Dictionary<string, string>())));
            Assert.True(unloadRequested);
        }
        finally
        {
            if (!unloadRequested)
                context.Unload();
        }
    }

    public void Dispose() => _temp.Delete(recursive: true);
}
