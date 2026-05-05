using Nuplane.Abstractions;
using Nuplane.Operational;
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
}
