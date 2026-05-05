using Nuplane.Abstractions;
using Nuplane.Store.State;

namespace Nuplane.Store.Tests.State;

public sealed class GraphActivationStateSerializationTests
{
    [Fact]
    public async Task SaveAsync_WithGraphMetadata_RoundTripsActiveGraphAndPackageRole()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nuplane-graph-state", Guid.NewGuid().ToString("N"));

        try
        {
            var stateFilePath = Path.Combine(tempRoot, "store-state.json");
            var activatedAtUtc = DateTimeOffset.Parse("2026-05-05T10:00:00Z");
            var state = new StoreStateRecord(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Plugin.Root"] = "1.0.0",
                    ["Plugin.Dependency"] = "1.0.0"
                },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, FailureRecord>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, SourceSnapshotRef>(StringComparer.OrdinalIgnoreCase),
                activatedAtUtc,
                new Dictionary<string, ActivePackageDescriptor>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Plugin.Root"] = new(
                        "Plugin.Root",
                        "1.0.0",
                        "feed-a",
                        "source-a",
                        "/packages/root",
                        activatedAtUtc,
                        "corr-1",
                        "graph-1",
                        "generation-1",
                        ActivePackageRole.Root,
                        ["Plugin.Root"],
                        [],
                        Discoverable: true),
                    ["Plugin.Dependency"] = new(
                        "Plugin.Dependency",
                        "1.0.0",
                        "feed-a",
                        "source-a",
                        "/packages/dependency",
                        activatedAtUtc,
                        "corr-1",
                        "graph-1",
                        "generation-1",
                        ActivePackageRole.Dependency,
                        ["Plugin.Root"],
                        ["Plugin.Root"],
                        Discoverable: false)
                },
                new Dictionary<string, GraphActivationRecord>(StringComparer.OrdinalIgnoreCase)
                {
                    ["graph-1"] = new(
                        "graph-1",
                        "generation-1",
                        ["Plugin.Root"],
                        ["Plugin.Root", "Plugin.Dependency"],
                        activatedAtUtc,
                        "corr-1",
                        GraphActivationStatus.Active)
                });

            var serializer = new StoreStateSerializer();
            await serializer.SaveAsync(stateFilePath, state, CancellationToken.None);

            var loaded = await serializer.LoadAsync(stateFilePath, CancellationToken.None);

            var graph = Assert.Single(loaded.ActiveGraphsByIdNormalized).Value;
            Assert.Equal("graph-1", graph.GraphId);
            Assert.Equal("generation-1", graph.GenerationId);
            Assert.Equal(GraphActivationStatus.Active, graph.Status);
            Assert.Equal(["Plugin.Root", "Plugin.Dependency"], graph.NodePackageIds);

            var dependency = loaded.ActivePackageDescriptorsByIdNormalized["Plugin.Dependency"];
            Assert.Equal("graph-1", dependency.GraphId);
            Assert.Equal("generation-1", dependency.GraphGenerationId);
            Assert.Equal(ActivePackageRole.Dependency, dependency.PackageRole);
            Assert.False(dependency.Discoverable);
            Assert.Equal(["Plugin.Root"], dependency.DependencyOfPackageIds);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task LoadAsync_WithLegacyState_NormalizesGraphMetadataDefaults()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nuplane-legacy-graph-state", Guid.NewGuid().ToString("N"));

        try
        {
            var stateFilePath = Path.Combine(tempRoot, "store-state.json");
            var serializer = new StoreStateSerializer();
            var state = new StoreStateRecord(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Plugin.Root"] = "1.0.0"
                },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, FailureRecord>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, SourceSnapshotRef>(StringComparer.OrdinalIgnoreCase),
                DateTimeOffset.Parse("2026-05-05T10:00:00Z"),
                new Dictionary<string, ActivePackageDescriptor>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Plugin.Root"] = new("Plugin.Root", "1.0.0", "feed-a", "source-a", "/packages/root", DateTimeOffset.UtcNow, "corr-legacy")
                });

            await serializer.SaveAsync(stateFilePath, state, CancellationToken.None);

            var loaded = await serializer.LoadAsync(stateFilePath, CancellationToken.None);

            Assert.Empty(loaded.ActiveGraphsByIdNormalized);
            var descriptor = loaded.ActivePackageDescriptorsByIdNormalized["Plugin.Root"];
            Assert.Equal("Plugin.Root", descriptor.GraphId);
            Assert.Equal("corr-legacy", descriptor.GraphGenerationId);
            Assert.Equal(ActivePackageRole.Root, descriptor.PackageRole);
            Assert.True(descriptor.Discoverable);
            Assert.Equal(["Plugin.Root"], descriptor.RootPackageIds);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}
