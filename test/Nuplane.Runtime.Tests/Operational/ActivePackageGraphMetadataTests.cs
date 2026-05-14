using Nuplane.Abstractions;
using Nuplane.Operational;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Operational;

public sealed class ActivePackageGraphMetadataTests
{
    [Fact]
    public void BuildNextDescriptors_WhenGraphMetadataIsNotProvided_UsesLegacyRootDefaults()
    {
        var activatedAtUtc = DateTimeOffset.Parse("2026-05-05T10:00:00Z");
        var state = new StoreStateRecord(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, FailureRecord>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SourceSnapshotRef>(StringComparer.OrdinalIgnoreCase),
            activatedAtUtc,
            new Dictionary<string, ActivePackageDescriptor>(StringComparer.OrdinalIgnoreCase));
        var resolvedPackage = new ResolvedPackage(
            "Plugin.Root",
            "1.0.0",
            "feed-a",
            "/packages/root",
            activatedAtUtc,
            "source-a");

        var descriptors = ActivePackageCatalogMapper.BuildNextDescriptors(
            state,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Plugin.Root"] = "1.0.0"
            },
            [resolvedPackage],
            new PackageChangeSet([resolvedPackage], [], [], "corr-1", activatedAtUtc),
            "corr-1",
            activatedAtUtc);

        var descriptor = Assert.Single(descriptors).Value;
        Assert.Equal("Plugin.Root", descriptor.GraphId);
        Assert.Equal("corr-1", descriptor.GraphGenerationId);
        Assert.Equal(ActivePackageRole.Root, descriptor.PackageRole);
        Assert.True(descriptor.Discoverable);
        Assert.Equal(["Plugin.Root"], descriptor.RootPackageIds);
        Assert.Empty(descriptor.DependencyOfPackageIds);
    }

    [Fact]
    public void ToActivePackage_WithDependencyDescriptor_PreservesGraphMetadata()
    {
        var descriptor = new ActivePackageDescriptor(
            "Plugin.Dependency",
            "1.0.0",
            "feed-a",
            "source-a",
            "/packages/dependency",
            DateTimeOffset.Parse("2026-05-05T10:00:00Z"),
            "corr-1",
            "graph-1",
            "generation-1",
            ActivePackageRole.Dependency,
            ["Plugin.Root"],
            ["Plugin.Root"],
            Discoverable: false);

        var package = descriptor.ToActivePackage();

        Assert.Equal("graph-1", package.GraphId);
        Assert.Equal("generation-1", package.GraphGenerationId);
        Assert.Equal(ActivePackageRole.Dependency, package.PackageRole);
        Assert.False(package.Discoverable);
        Assert.Equal(["Plugin.Root"], package.RootPackageIds);
        Assert.Equal(["Plugin.Root"], package.DependencyOfPackageIds);
    }

    [Fact]
    public void BuildNextDescriptors_WithDependencyGraph_MarksDependencyNodesSupportOnly()
    {
        var activatedAtUtc = DateTimeOffset.Parse("2026-05-05T10:00:00Z");
        var state = new StoreStateRecord(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, FailureRecord>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SourceSnapshotRef>(StringComparer.OrdinalIgnoreCase),
            activatedAtUtc,
            new Dictionary<string, ActivePackageDescriptor>(StringComparer.OrdinalIgnoreCase));
        var root = new ResolvedPackage("Plugin.Root", "1.0.0", "feed-a", "/packages/root", activatedAtUtc, "source-a");
        var dependency = new ResolvedPackage("Plugin.Dependency", "1.0.0", "feed-a", "/packages/dependency", activatedAtUtc, "dependency-of:Plugin.Root");
        var rootNode = Node(root, PackageNodeRole.Root);
        var dependencyNode = Node(dependency, PackageNodeRole.Dependency);
        var graph = new ResolvedPackageGraph(
            "graph-1",
            "generation-1",
            "net10.0",
            [rootNode],
            [rootNode, dependencyNode],
            [new DependencyEdge(root.Id, root.Version, dependency.Id, "[1.0.0, )", dependency.Version, "net10.0", Optional: false)],
            [],
            activatedAtUtc);

        var descriptors = ActivePackageCatalogMapper.BuildNextDescriptors(
            state,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [root.Id] = root.Version,
                [dependency.Id] = dependency.Version
            },
            [root, dependency],
            new PackageChangeSet([root, dependency], [], [], "corr-1", activatedAtUtc),
            "corr-1",
            activatedAtUtc,
            [graph]);

        var dependencyDescriptor = descriptors["Plugin.Dependency"];
        Assert.Equal(ActivePackageRole.Dependency, dependencyDescriptor.PackageRole);
        Assert.False(dependencyDescriptor.Discoverable);
    }

    [Fact]
    public void BuildNextDescriptors_WhenExistingDescriptorHasStaleDiscoverability_UpdatesGraphMetadata()
    {
        var activatedAtUtc = DateTimeOffset.Parse("2026-05-05T10:00:00Z");
        var root = new ResolvedPackage("Plugin.Root", "1.0.0", "feed-a", "/packages/root", activatedAtUtc, "source-a");
        var dependency = new ResolvedPackage("Plugin.Dependency", "1.0.0", "feed-a", "/packages/dependency", activatedAtUtc, "dependency-of:Plugin.Root");
        var currentState = StoreStateRecord.Empty() with
        {
            ActivePackageDescriptorsById = new(StringComparer.OrdinalIgnoreCase)
            {
                [dependency.Id] = new(
                    dependency.Id,
                    dependency.Version,
                    "feed-a",
                    "dependency-of:Plugin.Root",
                    dependency.InstallPath,
                    activatedAtUtc,
                    "corr-old",
                    "graph-old",
                    "generation-old",
                    ActivePackageRole.Dependency,
                    [root.Id],
                    [root.Id],
                    Discoverable: true)
            }
        };
        var graph = Graph(
            "graph-new",
            "generation-new",
            root,
            dependency,
            Node(dependency, PackageNodeRole.Dependency));

        var descriptors = ActivePackageCatalogMapper.BuildNextDescriptors(
            currentState,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [root.Id] = root.Version,
                [dependency.Id] = dependency.Version
            },
            [root, dependency],
            new PackageChangeSet([], [], [], "corr-new", activatedAtUtc),
            "corr-new",
            activatedAtUtc,
            [graph]);

        var descriptor = descriptors[dependency.Id];
        Assert.Equal("graph-new", descriptor.GraphId);
        Assert.Equal("generation-new", descriptor.GraphGenerationId);
        Assert.False(descriptor.Discoverable);
    }

    [Fact]
    public void BuildNextDescriptors_WhenExistingDescriptorHasSameMetadataInDifferentOrder_PreservesExistingActivation()
    {
        var activatedAtUtc = DateTimeOffset.Parse("2026-05-05T10:00:00Z");
        var rootA = new ResolvedPackage("Plugin.RootA", "1.0.0", "feed-a", "/packages/root-a", activatedAtUtc, "source-a");
        var rootB = new ResolvedPackage("Plugin.RootB", "1.0.0", "feed-a", "/packages/root-b", activatedAtUtc, "source-a");
        var dependency = new ResolvedPackage("Plugin.Dependency", "1.0.0", "feed-a", "/packages/dependency", activatedAtUtc, "source-a");
        var dependencyNode = Node(dependency, PackageNodeRole.Dependency);
        var graphA = Graph("graph-a", "generation-a", rootA, dependency, dependencyNode);
        var graphB = Graph("graph-b", "generation-b", rootB, dependency, dependencyNode);
        var currentState = StoreStateRecord.Empty() with
        {
            ActivePackageDescriptorsById = new(StringComparer.OrdinalIgnoreCase)
            {
                [dependency.Id] = new(
                    dependency.Id,
                    dependency.Version,
                    "feed-a",
                    "source-a",
                    dependency.InstallPath,
                    activatedAtUtc,
                    "corr-old",
                    "graph-a",
                    "generation-a",
                    ActivePackageRole.Dependency,
                    [rootB.Id, rootA.Id],
                    [rootB.Id, rootA.Id],
                    Discoverable: false)
            }
        };

        var descriptors = ActivePackageCatalogMapper.BuildNextDescriptors(
            currentState,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [rootA.Id] = rootA.Version,
                [rootB.Id] = rootB.Version,
                [dependency.Id] = dependency.Version
            },
            [rootA, rootB, dependency],
            new PackageChangeSet([], [], [], "corr-new", activatedAtUtc.AddMinutes(1)),
            "corr-new",
            activatedAtUtc.AddMinutes(1),
            [graphA, graphB]);

        var descriptor = descriptors[dependency.Id];
        Assert.Equal("corr-old", descriptor.ActivationCorrelationId);
        Assert.Equal(activatedAtUtc, descriptor.ActivatedAtUtc);
    }

    [Fact]
    public void BuildNextDescriptors_WithSharedDependency_PreservesFirstGraphAndMergesParents()
    {
        var activatedAtUtc = DateTimeOffset.Parse("2026-05-05T10:00:00Z");
        var state = new StoreStateRecord(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, FailureRecord>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SourceSnapshotRef>(StringComparer.OrdinalIgnoreCase),
            activatedAtUtc,
            new Dictionary<string, ActivePackageDescriptor>(StringComparer.OrdinalIgnoreCase));
        var rootA = new ResolvedPackage("Plugin.RootA", "1.0.0", "feed-a", "/packages/root-a", activatedAtUtc, "source-a");
        var rootB = new ResolvedPackage("Plugin.RootB", "1.0.0", "feed-a", "/packages/root-b", activatedAtUtc, "source-a");
        var dependency = new ResolvedPackage("Plugin.Dependency", "1.0.0", "feed-a", "/packages/dependency", activatedAtUtc, "source-a");
        var dependencyNode = Node(dependency, PackageNodeRole.Dependency);
        var graphA = Graph("graph-a", "generation-a", rootA, dependency, dependencyNode);
        var graphB = Graph("graph-b", "generation-b", rootB, dependency, dependencyNode);

        var descriptors = ActivePackageCatalogMapper.BuildNextDescriptors(
            state,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [rootA.Id] = rootA.Version,
                [rootB.Id] = rootB.Version,
                [dependency.Id] = dependency.Version
            },
            [rootA, rootB, dependency],
            new PackageChangeSet([rootA, rootB, dependency], [], [], "corr-1", activatedAtUtc),
            "corr-1",
            activatedAtUtc,
            [graphA, graphB]);

        var dependencyDescriptor = descriptors["Plugin.Dependency"];
        Assert.Equal("graph-a", dependencyDescriptor.GraphId);
        Assert.Equal("generation-a", dependencyDescriptor.GraphGenerationId);
        Assert.Equal(["Plugin.RootA", "Plugin.RootB"], dependencyDescriptor.RootPackageIds);
        Assert.Equal(["Plugin.RootA", "Plugin.RootB"], dependencyDescriptor.DependencyOfPackageIds);
    }

    [Fact]
    public void BuildActiveGraphRecords_WhenDependencyVersionChanges_RemovesStaleGraphRecord()
    {
        var activatedAtUtc = DateTimeOffset.Parse("2026-05-05T10:00:00Z");
        var root = new ResolvedPackage("Plugin.Root", "1.0.0", "feed-a", "/packages/root", activatedAtUtc, "source-a");
        var oldDependency = new ResolvedPackage("Plugin.Dependency", "1.0.0", "feed-a", "/packages/dependency/1.0.0", activatedAtUtc, "source-a");
        var newDependency = new ResolvedPackage("Plugin.Dependency", "2.0.0", "feed-a", "/packages/dependency/2.0.0", activatedAtUtc, "source-a");
        var oldGraph = Graph("graph-old", "generation-old", root, oldDependency, Node(oldDependency, PackageNodeRole.Dependency));
        var newGraph = Graph("graph-new", "generation-new", root, newDependency, Node(newDependency, PackageNodeRole.Dependency));
        var currentState = StoreStateRecord.Empty() with
        {
            ActiveGraphsById = new(StringComparer.OrdinalIgnoreCase)
            {
                ["graph-old"] = new(
                    oldGraph.GraphId,
                    oldGraph.GenerationId,
                    oldGraph.Roots.Select(static node => node.PackageId).ToArray(),
                    oldGraph.Nodes.Select(static node => node.PackageId).ToArray(),
                    activatedAtUtc,
                    "corr-old",
                    GraphActivationStatus.Active,
                    NodeVersionsByPackageId: oldGraph.Nodes.ToDictionary(static node => node.PackageId, static node => node.Version, StringComparer.OrdinalIgnoreCase))
            }
        };

        var records = ActivePackageCatalogMapper.BuildActiveGraphRecords(
            currentState,
            [newGraph],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [root.Id] = root.Version,
                [newDependency.Id] = newDependency.Version
            },
            "corr-new",
            activatedAtUtc);

        var record = Assert.Single(records).Value;
        Assert.Equal("graph-new", record.GraphId);
        Assert.Equal("2.0.0", record.NodeVersionsByPackageId!["Plugin.Dependency"]);
    }

    [Fact]
    public void BuildActiveGraphRecords_WhenSameRootSetResolvesExpandedGraph_ReplacesStaleGraphRecord()
    {
        var activatedAtUtc = DateTimeOffset.Parse("2026-05-05T10:00:00Z");
        var root = new ResolvedPackage("Plugin.Root", "1.0.0", "feed-a", "/packages/root", activatedAtUtc, "source-a");
        var dependency = new ResolvedPackage("Plugin.Dependency", "1.0.0", "feed-a", "/packages/dependency", activatedAtUtc, "source-a");
        var support = new ResolvedPackage("Plugin.Support", "1.0.0", "feed-a", "/packages/support", activatedAtUtc, "source-a");
        var oldGraph = Graph("graph-old", "generation-old", root, [dependency]);
        var newGraph = Graph("graph-new", "generation-new", root, [dependency, support]);
        var currentState = StateWithActiveGraph(oldGraph, activatedAtUtc);

        var records = ActivePackageCatalogMapper.BuildActiveGraphRecords(
            currentState,
            [newGraph],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [root.Id] = root.Version,
                [dependency.Id] = dependency.Version,
                [support.Id] = support.Version
            },
            "corr-new",
            activatedAtUtc);

        var record = Assert.Single(records).Value;
        Assert.Equal("graph-new", record.GraphId);
        Assert.Equal([root.Id, dependency.Id, support.Id], record.NodePackageIds);
    }

    [Fact]
    public void BuildActiveGraphRecords_WhenSameRootSetResolvedGraphIsNotActive_PreservesLastKnownGoodGraphRecord()
    {
        var activatedAtUtc = DateTimeOffset.Parse("2026-05-05T10:00:00Z");
        var root = new ResolvedPackage("Plugin.Root", "1.0.0", "feed-a", "/packages/root", activatedAtUtc, "source-a");
        var dependency = new ResolvedPackage("Plugin.Dependency", "1.0.0", "feed-a", "/packages/dependency", activatedAtUtc, "source-a");
        var support = new ResolvedPackage("Plugin.Support", "1.0.0", "feed-a", "/packages/support", activatedAtUtc, "source-a");
        var oldGraph = Graph("graph-old", "generation-old", root, [dependency]);
        var newGraph = Graph("graph-new", "generation-new", root, [dependency, support]);
        var currentState = StateWithActiveGraph(oldGraph, activatedAtUtc);

        var records = ActivePackageCatalogMapper.BuildActiveGraphRecords(
            currentState,
            [newGraph],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [root.Id] = root.Version,
                [dependency.Id] = dependency.Version
            },
            "corr-new",
            activatedAtUtc);

        var record = Assert.Single(records).Value;
        Assert.Equal("graph-old", record.GraphId);
    }

    private static ResolvedPackageNode Node(ResolvedPackage package, PackageNodeRole role) =>
        new(
            package.Id,
            package.Version,
            role,
            package.InstallPath,
            PackageSourceKind.RemoteFeed,
            package.SourceName,
            PackageContentHash: null,
            RuntimeAssets: [],
            DiscoverableAssets: [],
            SupportAssets: []);

    private static ResolvedPackageGraph Graph(
        string graphId,
        string generationId,
        ResolvedPackage root,
        ResolvedPackage dependency,
        ResolvedPackageNode dependencyNode)
    {
        var rootNode = Node(root, PackageNodeRole.Root);
        return new(
            graphId,
            generationId,
            "net10.0",
            [rootNode],
            [rootNode, dependencyNode],
            [new DependencyEdge(root.Id, root.Version, dependency.Id, "[1.0.0, )", dependency.Version, "net10.0", Optional: false)],
            [],
            DateTimeOffset.Parse("2026-05-05T10:00:00Z"));
    }

    private static ResolvedPackageGraph Graph(
        string graphId,
        string generationId,
        ResolvedPackage root,
        IReadOnlyList<ResolvedPackage> dependencies)
    {
        var rootNode = Node(root, PackageNodeRole.Root);
        var dependencyNodes = dependencies
            .Select(static package => Node(package, PackageNodeRole.Dependency))
            .ToArray();
        var nodes = new[] { rootNode }
            .Concat(dependencyNodes)
            .ToArray();
        var edges = dependencies
            .Select((dependency, index) =>
            {
                var parent = index == 0 ? root : dependencies[index - 1];
                return new DependencyEdge(parent.Id, parent.Version, dependency.Id, "[1.0.0, )", dependency.Version, "net10.0", Optional: false);
            })
            .ToArray();

        return new(
            graphId,
            generationId,
            "net10.0",
            [rootNode],
            nodes,
            edges,
            [],
            DateTimeOffset.Parse("2026-05-05T10:00:00Z"));
    }

    private static StoreStateRecord StateWithActiveGraph(ResolvedPackageGraph graph, DateTimeOffset activatedAtUtc) =>
        StoreStateRecord.Empty() with
        {
            ActiveGraphsById = new(StringComparer.OrdinalIgnoreCase)
            {
                [graph.GraphId] = new(
                    graph.GraphId,
                    graph.GenerationId,
                    graph.Roots.Select(static node => node.PackageId).ToArray(),
                    graph.Nodes.Select(static node => node.PackageId).ToArray(),
                    activatedAtUtc,
                    "corr-old",
                    GraphActivationStatus.Active,
                    NodeVersionsByPackageId: graph.Nodes.ToDictionary(static node => node.PackageId, static node => node.Version, StringComparer.OrdinalIgnoreCase))
            }
        };
}
