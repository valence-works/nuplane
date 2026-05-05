using Nuplane.Abstractions;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;

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
        string? dependencyVersionRange = null)
    {
        var installPath = Path.Combine(tempRoot, packageId, version);
        Directory.CreateDirectory(installPath);
        File.WriteAllText(Path.Combine(installPath, $"{packageId}.nuspec"), CreateNuspec(packageId, version, dependencyId, dependencyVersionRange));
        return new ResolvedPackage(packageId, version, "test-feed", installPath, DateTimeOffset.UtcNow, "test-source");
    }

    private static string CreateNuspec(
        string packageId,
        string version,
        string? dependencyId,
        string? dependencyVersionRange) =>
        $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>{{packageId}}</id>
            <version>{{version}}</version>
            <authors>test</authors>
            <description>Test package</description>
            {{CreateDependencies(dependencyId, dependencyVersionRange)}}
          </metadata>
        </package>
        """;

    private static string CreateDependencies(string? dependencyId, string? dependencyVersionRange) =>
        string.IsNullOrWhiteSpace(dependencyId) || string.IsNullOrWhiteSpace(dependencyVersionRange)
            ? string.Empty
            : $"<dependencies><dependency id=\"{dependencyId}\" version=\"{dependencyVersionRange}\" /></dependencies>";

    private sealed class StubPackageResolver(IReadOnlyDictionary<string, ResolvedPackage> packages) : IPackageResolver
    {
        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken) =>
            packages.TryGetValue(request.Id, out var package)
                ? Task.FromResult(package)
                : Task.FromException<ResolvedPackage>(new InvalidOperationException($"Package '{request.Id}' was not configured."));
    }

    private sealed class PassthroughRetryPolicy : IReconciliationRetryPolicy
    {
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) => operation(cancellationToken);
    }
}
