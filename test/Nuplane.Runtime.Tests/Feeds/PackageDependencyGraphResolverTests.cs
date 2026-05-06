using Nuplane.Abstractions;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;
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
