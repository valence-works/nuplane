using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Loading;

namespace Nuplane.Loading.Tests;

public sealed class PackageStoreProtectionContextTests
{
    [Fact]
    public void LoadModeAdvisorContext_PreservesPositionalConstructionAndDeconstruction()
    {
        IReadOnlyList<ResolvedPackage> packages = [];
        IReadOnlyDictionary<string, PackageLoadMode> overrides = new Dictionary<string, PackageLoadMode>();
        var context = new LoadModeAdvisorContext(
            "graph-key",
            packages,
            PackageLoadModeSelectionPolicy.Automatic,
            PackageLoadMode.Collectible,
            overrides);

        var (graphKey, deconstructedPackages, selectionPolicy, defaultLoadMode, deconstructedOverrides) = context;

        Assert.Equal("graph-key", graphKey);
        Assert.Same(packages, deconstructedPackages);
        Assert.Equal(PackageLoadModeSelectionPolicy.Automatic, selectionPolicy);
        Assert.Equal(PackageLoadMode.Collectible, defaultLoadMode);
        Assert.Same(overrides, deconstructedOverrides);
        Assert.Empty(context.GraphUseLeases);
    }

    [Fact]
    public void LoadModeAdvisorContext_CopiesEmptyLeaseListAndExposesReadOnlyCollection()
    {
        var supplied = Array.Empty<PackageGraphUseLease>();
        var context = CreateAdvisorContext() with { GraphUseLeases = supplied };

        Assert.NotSame(supplied, context.GraphUseLeases);
        Assert.Empty(context.GraphUseLeases);
        Assert.Throws<NotSupportedException>(() => ((IList<PackageGraphUseLease>)context.GraphUseLeases).Add(null!));
    }

    [Fact]
    public void LoadModeAdvisorContext_RejectsNullLeaseListAndNullItems()
    {
        var context = CreateAdvisorContext();

        Assert.Throws<ArgumentNullException>(() => _ = context with { GraphUseLeases = null! });
        Assert.Throws<ArgumentException>(() => _ = context with { GraphUseLeases = [null!] });
    }

    [Fact]
    public void PackageActivationContext_PreservesPositionalConstructionAndDeconstruction()
    {
        IReadOnlyList<ResolvedPackage> packages = [];
        var context = new PackageActivationContext("graph-key", PackageLoadMode.HostIntegrated, packages);

        var (graphKey, loadMode, deconstructedPackages) = context;

        Assert.Equal("graph-key", graphKey);
        Assert.Equal(PackageLoadMode.HostIntegrated, loadMode);
        Assert.Same(packages, deconstructedPackages);
        Assert.Empty(context.GraphUseLeases);
    }

    [Fact]
    public void PackageActivationContext_CopiesEmptyLeaseListAndExposesReadOnlyCollection()
    {
        var supplied = Array.Empty<PackageGraphUseLease>();
        var context = CreateActivationContext() with { GraphUseLeases = supplied };

        Assert.NotSame(supplied, context.GraphUseLeases);
        Assert.Empty(context.GraphUseLeases);
        Assert.Throws<NotSupportedException>(() => ((IList<PackageGraphUseLease>)context.GraphUseLeases).Add(null!));
    }

    [Fact]
    public void PackageActivationContext_RejectsNullLeaseListAndNullItems()
    {
        var context = CreateActivationContext();

        Assert.Throws<ArgumentNullException>(() => _ = context with { GraphUseLeases = null! });
        Assert.Throws<ArgumentException>(() => _ = context with { GraphUseLeases = [null!] });
    }

    private static LoadModeAdvisorContext CreateAdvisorContext() =>
        new("graph-key", [], PackageLoadModeSelectionPolicy.Automatic, PackageLoadMode.Collectible, new Dictionary<string, PackageLoadMode>());

    private static PackageActivationContext CreateActivationContext() =>
        new("graph-key", PackageLoadMode.Collectible, []);
}
