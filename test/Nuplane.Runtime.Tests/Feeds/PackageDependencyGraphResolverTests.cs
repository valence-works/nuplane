using Nuplane.Abstractions;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;
using Nuplane.Runtime.Tests.TestSupport;
using System.Reflection;
using System.Reflection.Emit;

namespace Nuplane.Runtime.Tests.Feeds;

public sealed class PackageDependencyGraphResolverTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), $"nuplane-graph-resolver-{Guid.NewGuid():N}");

    [Fact]
    public async Task ResolveAsync_RootOnlyDesiredInput_ResolvesRootAndDependencyGraph()
    {
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Plugin.Dependency", dependencyVersionRange: "[1.0.0]");
        var dependency = CreateInstalledPackage("Plugin.Dependency", "1.0.0");
        var resolver = new StubPackageResolver(
            new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["Plugin.Dependency"] = dependency
            });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        Assert.Equal(["Plugin.Dependency", "Plugin.Root"], result.ResolvedPackages.Select(static package => package.Id).Order(StringComparer.OrdinalIgnoreCase));
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Equal(["Plugin.Root"], graph.Roots.Select(static node => node.PackageId));
        Assert.Contains(graph.Nodes, static node => node.PackageId == "Plugin.Dependency" && node.Role == PackageNodeRole.Dependency);
        var edge = Assert.Single(graph.Edges);
        Assert.Equal("Plugin.Root", edge.FromPackageId);
        Assert.Equal("Plugin.Dependency", edge.ToPackageId);
        Assert.Equal("[1.0.0]", edge.RequestedVersionRange);
    }

    [Fact]
    public async Task ResolveAsync_DependencyRequest_DoesNotPinRootFeed()
    {
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Plugin.Dependency", dependencyVersionRange: "[1.0.0]");
        var dependency = CreateInstalledPackage("Plugin.Dependency", "1.0.0");
        var resolver = new StubPackageResolver(
            new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["Plugin.Dependency"] = dependency
            });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "root-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        var dependencyRequest = Assert.Single(resolver.Requests);
        Assert.Equal("Plugin.Dependency", dependencyRequest.Id);
        Assert.Null(dependencyRequest.FeedName);
        Assert.Equal("dependency-of:Plugin.Root", dependencyRequest.SourceName);
    }

    [Fact]
    public async Task ResolveAsync_BareDependencyVersion_TreatsVersionAsInclusiveMinimum()
    {
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Plugin.Dependency", dependencyVersionRange: "8.0.2");
        var dependency = CreateInstalledPackage("Plugin.Dependency", "10.0.3");
        var resolver = new VersionRangePackageResolver(
            new Dictionary<string, IReadOnlyList<ResolvedPackage>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Plugin.Dependency"] = [dependency]
            });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Contains(graph.Nodes, static node => node.PackageId == "Plugin.Dependency" && node.Version == "10.0.3");
        var edge = Assert.Single(graph.Edges);
        Assert.Equal("[8.0.2,)", edge.RequestedVersionRange);
        var request = Assert.Single(resolver.Requests);
        Assert.Equal("[8.0.2,)", request.VersionRange);
    }

    [Fact]
    public async Task ResolveAsync_WhenHigherDirectDependencySatisfiesTransitiveBaseline_ReusesSelectedDependency()
    {
        var root = CreateInstalledPackage(
            "Plugin.Root",
            "1.0.0",
            dependenciesXml: """
                <dependencies>
                  <dependency id="Plugin.Direct" version="10.0.3" />
                  <dependency id="Plugin.Transitive" version="[1.0.0]" />
                </dependencies>
                """);
        var direct = CreateInstalledPackage("Plugin.Direct", "10.0.3");
        var transitive = CreateInstalledPackage("Plugin.Transitive", "1.0.0", dependencyId: "Plugin.Direct", dependencyVersionRange: "8.0.2");
        var resolver = new VersionRangePackageResolver(
            new Dictionary<string, IReadOnlyList<ResolvedPackage>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Plugin.Direct"] = [direct],
                ["Plugin.Transitive"] = [transitive]
            });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Single(graph.Nodes, static node => node.PackageId == "Plugin.Direct");
        Assert.Equal(2, graph.Edges.Count(static edge => edge.ToPackageId == "Plugin.Direct"));
        Assert.Single(resolver.Requests, static request => request.Id == "Plugin.Direct");
    }

    [Fact]
    public async Task ResolveAsync_MultipleRoots_UnifiesSharedDependencyWithNuGetLowestApplicableVersion()
    {
        var leftRoot = CreateInstalledPackage("Plugin.Left", "1.0.0", dependencyId: "Plugin.Shared", dependencyVersionRange: "1.0.0");
        var rightRoot = CreateInstalledPackage("Plugin.Right", "1.0.0", dependencyId: "Plugin.Shared", dependencyVersionRange: "2.0.0");
        var resolver = new VersionRangePackageResolver(
            new Dictionary<string, IReadOnlyList<ResolvedPackage>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Plugin.Shared"] =
                [
                    CreateInstalledPackage("Plugin.Shared", "1.0.0"),
                    CreateInstalledPackage("Plugin.Shared", "2.0.0"),
                    CreateInstalledPackage("Plugin.Shared", "3.0.0")
                ]
            });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var result = await sut.ResolveAsync(
            [
                new PackageRequest("Plugin.Left", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source"),
                new PackageRequest("Plugin.Right", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source")
            ],
            (request, _) => Task.FromResult(request.Id == "Plugin.Left" ? leftRoot : rightRoot),
            CancellationToken.None);

        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Equal(["Plugin.Left", "Plugin.Right"], graph.Roots.Select(static node => node.PackageId).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Single(graph.Nodes, static node => node.PackageId == "Plugin.Shared");
        Assert.Contains(graph.Nodes, static node => node.PackageId == "Plugin.Shared" && node.Version == "2.0.0");
        Assert.DoesNotContain(graph.Nodes, static node => node.PackageId == "Plugin.Shared" && node.Version == "3.0.0");
    }

    [Fact]
    public async Task ResolveAsync_WhenCancelledBeforeNuGetSolve_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0");
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase));
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) =>
            {
                cts.Cancel();
                return Task.FromResult(root);
            },
            cts.Token));
    }

    [Fact]
    public async Task ResolveAsync_WithNonNormalizedPackageVersion_MapsNuGetSelectedIdentityBackToResolvedPackage()
    {
        var root = CreateInstalledPackage("Plugin.Root", "1.0");
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase));
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        var package = Assert.Single(result.ResolvedPackages);
        Assert.Equal("Plugin.Root", package.Id);
        Assert.Equal("1.0", package.Version);
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Contains(graph.Nodes, static node => node.PackageId == "Plugin.Root" && node.Version == "1.0");
    }

    [Fact]
    public async Task ResolveAsync_WithMalformedDependencyVersionRange_ThrowsNamedInvalidRangeDiagnostic()
    {
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Plugin.Dependency", dependencyVersionRange: "latest");
        var dependency = CreateInstalledPackage("Plugin.Dependency", "1.0.0");
        var resolver = new StubPackageResolver(
            new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["Plugin.Dependency"] = dependency
            });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None));

        Assert.Contains("invalid version range 'latest'", exception.Message);
        Assert.Contains("Plugin.Dependency", exception.Message);
    }

    [Fact]
    public async Task ResolveAsync_DependencyProvidedByHost_DoesNotAcquireDependencyNode()
    {
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Nuplane.Abstractions", dependencyVersionRange: "[1.0.0]");
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase));
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "root-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        Assert.Empty(resolver.Requests);
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Single(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public async Task ResolveAsync_MicrosoftExtensionsDependency_DoesNotAcquireDependencyNode()
    {
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Microsoft.Extensions.Options", dependencyVersionRange: "[8.0.0]");
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase));
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "root-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        Assert.Empty(resolver.Requests);
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Single(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public async Task ResolveAsync_ElsaFrameworkDependency_DoesNotAcquireDependencyNode()
    {
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Elsa.Api.Common", dependencyVersionRange: "[3.7.0-rc1]");
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase));
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "root-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        Assert.Empty(resolver.Requests);
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Single(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public async Task ResolveAsync_DependencyAlreadyLoadedAsPlugin_StillAcquiresDependencyNode()
    {
        var loadedDependencyId = $"Plugin.LoadedDependency.{Guid.NewGuid():N}";
        AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(loadedDependencyId), AssemblyBuilderAccess.Run);
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: loadedDependencyId, dependencyVersionRange: "[1.0.0]");
        var dependency = CreateInstalledPackage(loadedDependencyId, "1.0.0");
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
        {
            [loadedDependencyId] = dependency
        });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "root-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Contains(graph.Nodes, node => string.Equals(node.PackageId, loadedDependencyId, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(resolver.Requests, request => string.Equals(request.Id, loadedDependencyId, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResolveAsync_DependencyDllExistsInAppBase_StillAcquiresDependencyNode()
    {
        var dependencyId = $"Plugin.AppBaseDependency.{Guid.NewGuid():N}";
        var appBaseDll = Path.Combine(AppContext.BaseDirectory, $"{dependencyId}.dll");
        File.WriteAllBytes(appBaseDll, []);
        try
        {
            var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: dependencyId, dependencyVersionRange: "[1.0.0]");
            var dependency = CreateInstalledPackage(dependencyId, "1.0.0");
            var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                [dependencyId] = dependency
            });
            var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

            var result = await sut.ResolveAsync(
                [new PackageRequest("Plugin.Root", "[1.0.0]", "root-feed", PackageUpdatePolicy.Exact, "test-source")],
                (_, _) => Task.FromResult(root),
                CancellationToken.None);

            var graph = Assert.Single(result.ResolvedGraphs);
            Assert.Contains(graph.Nodes, node => string.Equals(node.PackageId, dependencyId, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(resolver.Requests, request => string.Equals(request.Id, dependencyId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(appBaseDll);
        }
    }


    [Fact]
    public async Task ResolveAsync_WithFrameworkSpecificDependencyGroups_SelectsCompatibleHostGroupOnly()
    {
        var root = CreateInstalledPackage(
            "Plugin.Root",
            "1.0.0",
            dependenciesXml: """
                <dependencies>
                  <group targetFramework="net8.0">
                    <dependency id="Plugin.Compatible" version="[1.0.0]" />
                  </group>
                  <group targetFramework="net472">
                    <dependency id="Plugin.Legacy" version="[1.0.0]" />
                  </group>
                </dependencies>
                """);
        var compatible = CreateInstalledPackage("Plugin.Compatible", "1.0.0");
        var legacy = CreateInstalledPackage("Plugin.Legacy", "1.0.0");
        var resolver = new StubPackageResolver(
            new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["Plugin.Compatible"] = compatible,
                ["Plugin.Legacy"] = legacy
            });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Contains(graph.Nodes, static node => node.PackageId == "Plugin.Compatible");
        Assert.DoesNotContain(graph.Nodes, static node => node.PackageId == "Plugin.Legacy");
        var edge = Assert.Single(graph.Edges);
        Assert.Equal("Plugin.Compatible", edge.ToPackageId);
    }

    [Fact]
    public async Task ResolveAsync_WithDependencyCycle_ThrowsCycleDiagnostic()
    {
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Plugin.Dependency", dependencyVersionRange: "[1.0.0]");
        var dependency = CreateInstalledPackage("Plugin.Dependency", "1.0.0", dependencyId: "Plugin.Root", dependencyVersionRange: "[1.0.0]");
        var resolver = new StubPackageResolver(
            new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["Plugin.Root"] = root,
                ["Plugin.Dependency"] = dependency
            });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None));

        Assert.Contains("Dependency cycle detected", exception.Message);
        Assert.Contains("Plugin.Root@1.0.0 -> Plugin.Dependency@1.0.0 -> Plugin.Root@1.0.0", exception.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private ResolvedPackage CreateInstalledPackage(
        string packageId,
        string version,
        string? dependencyId = null,
        string? dependencyVersionRange = null,
        string? dependenciesXml = null)
    {
        var installPath = Path.Combine(tempRoot, packageId, version);
        Directory.CreateDirectory(installPath);
        File.WriteAllText(Path.Combine(installPath, $"{packageId}.nuspec"), CreateNuspec(packageId, version, dependencyId, dependencyVersionRange, dependenciesXml));
        return new ResolvedPackage(packageId, version, "test-feed", installPath, DateTimeOffset.UtcNow, "test-source");
    }

    private static string CreateNuspec(
        string packageId,
        string version,
        string? dependencyId,
        string? dependencyVersionRange,
        string? dependenciesXml) =>
        $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>{{packageId}}</id>
            <version>{{version}}</version>
            <authors>test</authors>
            <description>Test package</description>
            {{dependenciesXml ?? CreateDependencies(dependencyId, dependencyVersionRange)}}
          </metadata>
        </package>
        """;

    private static string CreateDependencies(string? dependencyId, string? dependencyVersionRange) =>
        string.IsNullOrWhiteSpace(dependencyId) || string.IsNullOrWhiteSpace(dependencyVersionRange)
            ? string.Empty
            : $"<dependencies><dependency id=\"{dependencyId}\" version=\"{dependencyVersionRange}\" /></dependencies>";

    private sealed class StubPackageResolver(IReadOnlyDictionary<string, ResolvedPackage> packages) : IPackageResolver
    {
        public List<PackageRequest> Requests { get; } = [];

        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return packages.TryGetValue(request.Id, out var package)
                ? Task.FromResult(package)
                : Task.FromException<ResolvedPackage>(new InvalidOperationException($"Package '{request.Id}' was not configured."));
        }
    }

    private sealed class PassthroughRetryPolicy : IReconciliationRetryPolicy
    {
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) => operation(cancellationToken);
    }
}
