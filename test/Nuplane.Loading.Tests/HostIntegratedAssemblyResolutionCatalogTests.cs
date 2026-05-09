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

    [Fact]
    public void PublishGraph_WhenPublishingDifferentGraphs_PreservesPreviouslyPublishedEntries()
    {
        var sut = new HostIntegratedAssemblyResolutionCatalog();
        var firstAssembly = typeof(FixtureMarker).Assembly;
        var secondAssembly = typeof(HostIntegratedAssemblyResolutionCatalogTests).Assembly;

        sut.PublishGraph(
            "graph:first",
            [new PackageLoadModeSelection("pkg-a", "1.0.0", PackageLoadMode.HostIntegrated, "default", "graph:first")],
            new Dictionary<string, IReadOnlyList<Assembly>>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a@1.0.0"] = [firstAssembly]
            });
        sut.PublishGraph(
            "graph:second",
            [new PackageLoadModeSelection("pkg-b", "1.0.0", PackageLoadMode.HostIntegrated, "default", "graph:second")],
            new Dictionary<string, IReadOnlyList<Assembly>>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-b@1.0.0"] = [secondAssembly]
            });

        Assert.True(sut.TryResolve(firstAssembly.GetName(), out var resolvedFirst, out _));
        Assert.True(sut.TryResolve(secondAssembly.GetName(), out var resolvedSecond, out _));
        Assert.Same(firstAssembly, resolvedFirst);
        Assert.Same(secondAssembly, resolvedSecond);
    }

    [Fact]
    public void RemovePackage_WhenEntryExists_IncrementsGeneration()
    {
        var sut = new HostIntegratedAssemblyResolutionCatalog();
        var assembly = typeof(FixtureMarker).Assembly;
        sut.PublishGraph(
            "graph:first",
            [new PackageLoadModeSelection("pkg-a", "1.0.0", PackageLoadMode.HostIntegrated, "default", "graph:first")],
            new Dictionary<string, IReadOnlyList<Assembly>>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a@1.0.0"] = [assembly]
            });
        var generation = sut.Generation;

        sut.RemovePackage("pkg-a", "1.0.0");

        Assert.True(sut.Generation > generation);
        Assert.False(sut.TryResolve(assembly.GetName(), out _, out _));
    }

    [Fact]
    public void ValidateCanPublishGraph_WhenCandidateConflictsWithExistingEntry_FailsBeforeLoad()
    {
        var sut = new HostIntegratedAssemblyResolutionCatalog();
        var assembly = typeof(FixtureMarker).Assembly;
        sut.PublishGraph(
            "graph:first",
            [new PackageLoadModeSelection("pkg-a", "1.0.0", PackageLoadMode.HostIntegrated, "default", "graph:first")],
            new Dictionary<string, IReadOnlyList<Assembly>>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a@1.0.0"] = [assembly]
            });

        var ex = Assert.Throws<InvalidOperationException>(() => sut.ValidateCanPublishGraph(
            "graph:second",
            [new HostIntegratedAssemblyResolutionCandidate(assembly.GetName().Name!, new Version(99, 0, 0, 0), "pkg-b", "2.0.0", "graph:second")]));

        Assert.Contains("Host-integrated assembly conflict", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateCanPublishGraph_WhenSameAssemblyNameAndVersionComesFromDifferentPackage_FailsBeforeLoad()
    {
        var sut = new HostIntegratedAssemblyResolutionCatalog();
        var assemblyName = typeof(FixtureMarker).Assembly.GetName();

        var ex = Assert.Throws<InvalidOperationException>(() => sut.ValidateCanPublishGraph(
            "graph:ambiguous",
            [
                new HostIntegratedAssemblyResolutionCandidate(assemblyName.Name!, assemblyName.Version, "pkg-a", "1.0.0", "graph:ambiguous"),
                new HostIntegratedAssemblyResolutionCandidate(assemblyName.Name!, assemblyName.Version, "pkg-b", "1.0.0", "graph:ambiguous")
            ]));

        Assert.Contains("Host-integrated assembly conflict", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublishGraph_WhenSameAssemblyNameAndVersionComesFromDifferentPackage_KeepsLastKnownGoodGenerationVisible()
    {
        var sut = new HostIntegratedAssemblyResolutionCatalog();
        var assembly = typeof(FixtureMarker).Assembly;

        var ex = Assert.Throws<InvalidOperationException>(() => sut.PublishGraph(
            "graph:ambiguous",
            [
                new PackageLoadModeSelection("pkg-a", "1.0.0", PackageLoadMode.HostIntegrated, "default", "graph:ambiguous"),
                new PackageLoadModeSelection("pkg-b", "1.0.0", PackageLoadMode.HostIntegrated, "default", "graph:ambiguous")
            ],
            new Dictionary<string, IReadOnlyList<Assembly>>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a@1.0.0"] = [assembly],
                ["pkg-b@1.0.0"] = [assembly]
            }));

        Assert.Contains("Host-integrated assembly conflict", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, sut.Generation);
        Assert.False(sut.TryResolve(assembly.GetName(), out _, out _));
    }

    [Fact]
    public void TryResolve_WhenFullIdentityDoesNotMatch_ReturnsNotFound()
    {
        var sut = new HostIntegratedAssemblyResolutionCatalog();
        var assembly = typeof(FixtureMarker).Assembly;
        sut.PublishGraph(
            "graph:first",
            [new PackageLoadModeSelection("pkg-a", "1.0.0", PackageLoadMode.HostIntegrated, "default", "graph:first")],
            new Dictionary<string, IReadOnlyList<Assembly>>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a@1.0.0"] = [assembly]
            });
        var requestedName = assembly.GetName();
        requestedName.CultureName = "fr-FR";

        Assert.False(sut.TryResolve(requestedName, out var resolvedAssembly, out var diagnostic));
        Assert.Null(resolvedAssembly);
        Assert.Equal("not-found", diagnostic.Outcome);
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
