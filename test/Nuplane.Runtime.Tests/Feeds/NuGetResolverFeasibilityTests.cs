using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;

namespace Nuplane.Runtime.Tests.Feeds;

public sealed class NuGetResolverFeasibilityTests
{
    private readonly SourceRepository source = new(
        new PackageSource("https://feed.example/v3/index.json", "test-feed"),
        Enumerable.Empty<INuGetResourceProvider>());

    [Fact]
    public void Resolve_LowestDependencyBehavior_SelectsLowestApplicableDependencyVersion()
    {
        var resolved = Resolve(
            targetIds: ["Plugin.Root"],
            preferredVersions: [Identity("Plugin.Root", "1.0.0")],
            availablePackages:
            [
                Package("Plugin.Root", "1.0.0", Dependency("Microsoft.EntityFrameworkCore", "[10.0.3,)")),
                Package("Microsoft.EntityFrameworkCore", "10.0.3"),
                Package("Microsoft.EntityFrameworkCore", "10.0.4"),
                Package("Microsoft.EntityFrameworkCore", "11.0.0-preview.4.26230.115")
            ]);

        Assert.Contains(resolved, static package => package.Id == "Microsoft.EntityFrameworkCore" && package.Version.ToNormalizedString() == "10.0.3");
        Assert.DoesNotContain(resolved, static package => package.Id == "Microsoft.EntityFrameworkCore" && package.Version.ToNormalizedString().StartsWith("11.0.0-preview", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_DirectDependencyWins_ReusesDirectVersionForLowerTransitiveBaseline()
    {
        var resolved = Resolve(
            targetIds: ["Plugin.Root", "Plugin.Direct"],
            preferredVersions: [Identity("Plugin.Root", "1.0.0"), Identity("Plugin.Direct", "10.0.3")],
            availablePackages:
            [
                Package("Plugin.Root", "1.0.0", Dependency("Plugin.Direct", "[10.0.3]"), Dependency("Plugin.Transitive", "[1.0.0]")),
                Package("Plugin.Direct", "10.0.3"),
                Package("Plugin.Transitive", "1.0.0", Dependency("Plugin.Direct", "[8.0.2,)"))
            ]);

        Assert.Single(resolved, static package => package.Id == "Plugin.Direct");
        Assert.Contains(resolved, static package => package.Id == "Plugin.Direct" && package.Version.ToNormalizedString() == "10.0.3");
    }

    [Fact]
    public void Resolve_CousinDependencies_SelectsLowestVersionThatSatisfiesAllRanges()
    {
        var resolved = Resolve(
            targetIds: ["Plugin.Root"],
            preferredVersions: [Identity("Plugin.Root", "1.0.0")],
            availablePackages:
            [
                Package("Plugin.Root", "1.0.0", Dependency("Plugin.Left", "[1.0.0]"), Dependency("Plugin.Right", "[1.0.0]")),
                Package("Plugin.Left", "1.0.0", Dependency("Plugin.Shared", "[1.0.0,)")),
                Package("Plugin.Right", "1.0.0", Dependency("Plugin.Shared", "[2.0.0,)")),
                Package("Plugin.Shared", "1.0.0"),
                Package("Plugin.Shared", "2.0.0"),
                Package("Plugin.Shared", "3.0.0")
            ]);

        Assert.Contains(resolved, static package => package.Id == "Plugin.Shared" && package.Version.ToNormalizedString() == "2.0.0");
        Assert.DoesNotContain(resolved, static package => package.Id == "Plugin.Shared" && package.Version.ToNormalizedString() == "3.0.0");
    }

    [Fact]
    public void Resolve_MultipleTopLevelRoots_UnifiesSharedDependencyAcrossAggregateGraph()
    {
        var resolved = Resolve(
            targetIds: ["Plugin.Left", "Plugin.Right"],
            preferredVersions: [Identity("Plugin.Left", "1.0.0"), Identity("Plugin.Right", "1.0.0")],
            availablePackages:
            [
                Package("Plugin.Left", "1.0.0", Dependency("Plugin.Shared", "[1.0.0,)")),
                Package("Plugin.Right", "1.0.0", Dependency("Plugin.Shared", "[2.0.0,)")),
                Package("Plugin.Shared", "1.0.0"),
                Package("Plugin.Shared", "2.0.0"),
                Package("Plugin.Shared", "3.0.0")
            ]);

        Assert.Single(resolved, static package => package.Id == "Plugin.Shared");
        Assert.Contains(resolved, static package => package.Id == "Plugin.Shared" && package.Version.ToNormalizedString() == "2.0.0");
    }

    private IReadOnlyList<PackageIdentity> Resolve(
        IReadOnlyList<string> targetIds,
        IReadOnlyList<PackageIdentity> preferredVersions,
        IReadOnlyList<SourcePackageDependencyInfo> availablePackages)
    {
        var context = new PackageResolverContext(
            DependencyBehavior.Lowest,
            targetIds,
            requiredPackageIds: targetIds,
            packagesConfig: [],
            preferredVersions,
            availablePackages,
            [source.PackageSource],
            NullLogger.Instance);

        return new PackageResolver()
            .Resolve(context, CancellationToken.None)
            .OrderBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static package => package.Version)
            .ToArray();
    }

    private SourcePackageDependencyInfo Package(string id, string version, params PackageDependency[] dependencies) =>
        new(id, NuGetVersion.Parse(version), dependencies, listed: true, source);

    private static PackageDependency Dependency(string id, string versionRange) =>
        new(id, VersionRange.Parse(versionRange));

    private static PackageIdentity Identity(string id, string version) =>
        new(id, NuGetVersion.Parse(version));
}
