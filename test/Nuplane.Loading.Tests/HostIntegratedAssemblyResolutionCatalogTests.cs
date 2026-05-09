using System.Reflection;
using Nuplane.Loading.Tests.Fixtures;

namespace Nuplane.Loading.Tests;

public sealed class HostIntegratedAssemblyResolutionCatalogTests
{
    [Fact]
    public void PublishGraph_WhenReplacementConflicts_KeepsLastKnownGoodGenerationVisible()
    {
        var sut = new HostIntegratedAssemblyResolutionCatalog();
        var assembly = typeof(FixtureMarker).Assembly;
        var initialSelection = new PackageLoadModeSelection("pkg-a", "1.0.0", PackageLoadMode.HostIntegrated, "default", "graph:pkg-a");
        sut.PublishGraph(
            "graph:pkg-a",
            [initialSelection],
            new Dictionary<string, IReadOnlyList<Assembly>>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a@1.0.0"] = [assembly]
            });
        var generation = sut.Generation;

        var ex = Assert.Throws<InvalidOperationException>(() => sut.PublishGraph(
            "graph:conflict",
            [
                new PackageLoadModeSelection("pkg-a", "1.0.0", PackageLoadMode.HostIntegrated, "default", "graph:conflict"),
                new PackageLoadModeSelection("pkg-b", "2.0.0", PackageLoadMode.HostIntegrated, "default", "graph:conflict")
            ],
            new Dictionary<string, IReadOnlyList<Assembly>>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a@1.0.0"] = [assembly],
                ["pkg-b@2.0.0"] = [Assembly.LoadFile(GetConflictAssemblyPath())]
            }));

        Assert.Contains("Host-integrated assembly conflict", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(generation, sut.Generation);
        Assert.True(sut.TryResolve(assembly.GetName(), out var resolvedAssembly, out var diagnostic));
        Assert.Same(assembly, resolvedAssembly);
        Assert.Equal("success", diagnostic.Outcome);
    }

    private static string GetConflictAssemblyPath()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "Nuplane.Loading.Tests.Fixtures.Conflict",
            "bin",
            "Debug",
            "net10.0",
            "Nuplane.Loading.Tests.Fixtures.dll"));

        Assert.True(File.Exists(path), $"Expected conflict fixture at '{path}'.");
        return path;
    }
}
