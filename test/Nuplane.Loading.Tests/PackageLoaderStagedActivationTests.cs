using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Loading.Tests.Fixtures;

namespace Nuplane.Loading.Tests;

public sealed class PackageLoaderStagedActivationTests : IDisposable
{
    private static readonly IReadOnlySet<string> NoRejectedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("nuplane-staged-activation-");

    [Theory]
    [InlineData(PackageLoadMode.Collectible)]
    [InlineData(PackageLoadMode.HostIntegrated)]
    public async Task StageOrdinaryGraphsAsync_WhenPublishedGraphMatchesExactly_ReusesSessionsAndSharedContext(PackageLoadMode mode)
    {
        var catalog = new HostIntegratedAssemblyResolutionCatalog();
        var loader = new PackageLoader(hostIntegratedResolutionCatalog: catalog,
            options: Options.Create(new LoadingOptions { DefaultLoadMode = mode }));
        var root = Install("plugin", "1.0.0", typeof(FixtureMarker).Assembly);
        var dependency = Install("dependency", "3.0.0", typeof(PackageLoaderStagedActivationTests).Assembly);
        var facade = PackageMetadataTestSupport.Package("facade", "1.0.0",
            PackageMetadataTestSupport.CreateNoAssemblyInstallDir(_temp, "facade"));
        var initial = await loader.EnsureGraphLoadedAsync([[root, dependency, facade]], [], CancellationToken.None);
        Assert.Empty(initial.FailedByPackageId);
        var rootContext = ContextOf(loader, root);
        var catalogGeneration = catalog.Generation;

        using var batch = await loader.StageOrdinaryGraphsAsync([[facade, dependency, root]],
            Versions(root, dependency, facade), [], CancellationToken.None);
        Assert.Empty(batch.StagedContexts);
        Assert.Empty(batch.Result.FailedByPackageId);
        Assert.Equal(2, batch.Result.Loaded.Count);
        Assert.All(batch.Result.Loaded, session => Assert.Same(loader.Sessions[$"{session.PackageId}@{session.Version}"], session));
        Assert.Empty(batch.ValidateForCommit([ToActive(root), ToActive(dependency), ToActive(facade)], NoRejectedPackages));
        batch.Commit();

        Assert.Same(rootContext, ContextOf(loader, root));
        Assert.Same(rootContext, ContextOf(loader, dependency));
        Assert.True(loader.IsInertPackage(facade.Id, facade.Version));
        Assert.Equal(catalogGeneration, catalog.Generation);
        if (rootContext.IsCollectible)
            rootContext.Unload();
    }

    [Fact]
    public async Task StageOrdinaryGraphsAsync_WhenReplacementFails_PreservesTheSameVersionDependencySession()
    {
        var loader = new PackageLoader();
        var firstRoot = Install("plugin", "1.0.0", typeof(FixtureMarker).Assembly);
        var dependency = Install("dependency", "3.0.0", typeof(PackageLoaderStagedActivationTests).Assembly);
        var failedRoot = firstRoot with { Version = "2.0.0", InstallPath = Path.Combine(_temp.FullName, "missing") };
        var initial = await loader.EnsureGraphLoadedAsync([[firstRoot, dependency]], [], CancellationToken.None);
        Assert.Empty(initial.FailedByPackageId);
        var oldContext = ContextOf(loader, dependency);
        var oldSession = loader.Sessions["dependency@3.0.0"];

        using var batch = await loader.StageOrdinaryGraphsAsync([[failedRoot, dependency]], Versions(failedRoot, dependency), [], CancellationToken.None);
        Assert.Equal(["dependency", "plugin"], batch.Result.FailedByPackageId.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(batch.Result.Loaded);
        Assert.Empty(batch.ValidateForCommit([ToActive(firstRoot), ToActive(dependency)], NoRejectedPackages));
        batch.Commit();

        Assert.Same(oldContext, ContextOf(loader, firstRoot));
        Assert.Same(oldContext, ContextOf(loader, dependency));
        Assert.Same(oldSession, loader.Sessions["dependency@3.0.0"]);
        Assert.False(loader.TryGetContext(failedRoot.Id, failedRoot.Version, out _));
        oldContext.Unload();
    }

    [Fact]
    public async Task StageOrdinaryGraphsAsync_WhenCandidateIsDiscarded_UnloadsItAndKeepsPublishedProjection()
    {
        var loader = new PackageLoader();
        var first = Install("plugin", "1.0.0", typeof(FixtureMarker).Assembly);
        var replacement = Install(first.Id, "2.0.0", typeof(FixtureMarker).Assembly);
        await loader.EnsureGraphLoadedAsync([[first]], [], CancellationToken.None);
        var oldContext = ContextOf(loader, first);
        using var batch = await loader.StageOrdinaryGraphsAsync([[replacement]], Versions(replacement), [], CancellationToken.None);
        var stagedContext = Assert.Single(batch.StagedContexts);
        var unloaded = false;
        stagedContext.Unloading += _ => unloaded = true;

        Assert.Empty(batch.ValidateForCommit([ToActive(replacement)], NoRejectedPackages));
        Assert.False(loader.TryGetContext(replacement.Id, replacement.Version, out _));
        Assert.Same(oldContext, ContextOf(loader, first));
        batch.Dispose();

        Assert.True(unloaded);
        Assert.Same(oldContext, ContextOf(loader, first));
        Assert.False(loader.TryGetContext(replacement.Id, replacement.Version, out _));
        oldContext.Unload();
    }

    [Fact]
    public async Task ValidateForCommit_WhenUnchangedInstallsBelongToRejectedTopology_KeepsTheirOriginalContexts()
    {
        var loader = new PackageLoader();
        var root = Install("plugin", "1.0.0", typeof(FixtureMarker).Assembly);
        var dependency = Install("dependency", "3.0.0", typeof(PackageLoaderStagedActivationTests).Assembly);
        await loader.EnsureGraphLoadedAsync([[root], [dependency]], [], CancellationToken.None);
        var originalRoot = ContextOf(loader, root);
        var originalDependency = ContextOf(loader, dependency);
        Assert.NotSame(originalRoot, originalDependency);

        using var batch = await loader.StageOrdinaryGraphsAsync([[root, dependency]], Versions(root, dependency), [], CancellationToken.None);
        Assert.Empty(batch.Result.FailedByPackageId);
        Assert.Equal(2, batch.Result.Loaded.Count);
        Assert.Empty(batch.ValidateForCommit([ToActive(root), ToActive(dependency)],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Id }));
        batch.Commit();

        Assert.Same(originalRoot, ContextOf(loader, root));
        Assert.Same(originalDependency, ContextOf(loader, dependency));
        originalRoot.Unload();
        originalDependency.Unload();
    }

    [Fact]
    public async Task Commit_WhenCollectiblePackageIsRemoved_DefersItsUnloadUntilDisposal()
    {
        var loader = new PackageLoader();
        var package = Install("plugin", "1.0.0", typeof(FixtureMarker).Assembly);
        await loader.EnsureGraphLoadedAsync([[package]], [], CancellationToken.None);
        var context = ContextOf(loader, package);
        var unloaded = false;
        context.Unloading += _ => unloaded = true;

        using var batch = await loader.StageOrdinaryGraphsAsync([], Versions(), [], CancellationToken.None);
        Assert.Empty(batch.ValidateForCommit([], NoRejectedPackages));
        batch.Commit();

        Assert.Empty(loader.Sessions);
        Assert.False(loader.TryGetContext(package.Id, package.Version, out _));
        Assert.False(unloaded);
        batch.Dispose();
        Assert.True(unloaded);
    }

    [Fact]
    public async Task ValidateForCommit_WhenHostIntegratedAssemblyIsReplaced_SwapsCatalogOnlyAtCommit()
    {
        var catalog = new HostIntegratedAssemblyResolutionCatalog();
        var loader = new PackageLoader(
            hostIntegratedResolutionCatalog: catalog,
            options: Options.Create(new LoadingOptions { DefaultLoadMode = PackageLoadMode.HostIntegrated }));
        var first = Install("plugin", "1.0.0", typeof(FixtureMarker).Assembly);
        var replacement = Install(first.Id, "2.0.0", typeof(FixtureMarker).Assembly);
        await loader.EnsureGraphLoadedAsync([[first]], [], CancellationToken.None);
        var assemblyName = typeof(FixtureMarker).Assembly.GetName();
        var publishedGeneration = catalog.Generation;
        Assert.True(catalog.TryResolve(assemblyName, out var oldAssembly, out _));

        using var batch = await loader.StageOrdinaryGraphsAsync([[replacement]], Versions(replacement), [], CancellationToken.None);
        Assert.Empty(batch.Result.FailedByPackageId);
        Assert.Single(batch.Result.Loaded);
        Assert.Empty(batch.ValidateForCommit([ToActive(replacement)], NoRejectedPackages));

        Assert.True(catalog.TryResolve(assemblyName, out var beforeCommit, out _));
        Assert.Same(oldAssembly, beforeCommit);
        Assert.Equal(publishedGeneration, catalog.Generation);
        Assert.False(loader.TryGetContext(replacement.Id, replacement.Version, out _));

        batch.Commit();

        Assert.True(catalog.TryResolve(assemblyName, out var afterCommit, out _));
        Assert.NotSame(oldAssembly, afterCommit);
        Assert.Equal(replacement.InstallPath, Path.GetDirectoryName(afterCommit!.Location));
        Assert.Equal(publishedGeneration + 1, catalog.Generation);
        Assert.False(loader.TryGetContext(first.Id, first.Version, out _));
    }

    [Fact]
    public async Task ValidateForCommit_WhenFallbackRevealsAnotherAssemblyConflict_KeepsUnrelatedGraphEligible()
    {
        var catalog = new HostIntegratedAssemblyResolutionCatalog();
        var loader = new PackageLoader(
            hostIntegratedResolutionCatalog: catalog,
            options: Options.Create(new LoadingOptions { DefaultLoadMode = PackageLoadMode.HostIntegrated }));
        var previous = Install("plugin", "1.0.0", typeof(FixtureMarker).Assembly);
        var retained = Install("retained", "1.0.0", typeof(PackageLoaderStagedActivationTests).Assembly);
        var replacement = Install(previous.Id, "2.0.0", typeof(PackageLoaderStagedActivationTests).Assembly);
        var collidesWithFallback = Install("other-plugin", "1.0.0", typeof(FixtureMarker).Assembly);
        var independent = Install("independent", "1.0.0", typeof(PackageLoader).Assembly);
        var initial = await loader.EnsureGraphLoadedAsync([[previous], [retained]], [], CancellationToken.None);
        Assert.Empty(initial.FailedByPackageId);
        var originalContext = ContextOf(loader, previous);
        var initialGeneration = catalog.Generation;

        using var batch = await loader.StageOrdinaryGraphsAsync([[replacement], [collidesWithFallback], [independent]], Versions(replacement, retained, collidesWithFallback, independent), [],
            CancellationToken.None);
        var liveFailures = batch.Result.FailedByPackageId;
        var firstFailure = Assert.Single(liveFailures);
        Assert.Equal(replacement.Id, firstFailure.Key);
        Assert.Contains(typeof(PackageLoaderStagedActivationTests).Assembly.GetName().Name!, firstFailure.Value);
        Assert.DoesNotContain(batch.StagedContexts, context => context.Name == PackageLoader.BuildGraphKey([replacement]));
        Assert.Equal(2, batch.StagedContexts.Count);
        var rejected = liveFailures.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var secondFailures = batch.ValidateForCommit(
            [ToActive(previous), ToActive(retained), ToActive(collidesWithFallback), ToActive(independent)], rejected);
        var secondFailure = Assert.Single(secondFailures);
        Assert.Equal(collidesWithFallback.Id, secondFailure.Key);
        Assert.Contains(typeof(FixtureMarker).Assembly.GetName().Name!, secondFailure.Value);
        rejected.UnionWith(secondFailures.Keys);

        Assert.Empty(batch.ValidateForCommit([ToActive(previous), ToActive(retained), ToActive(independent)], rejected));
        Assert.Equal(rejected.Order(StringComparer.Ordinal), liveFailures.Keys.Order(StringComparer.Ordinal));
        Assert.False(loader.TryGetContext(independent.Id, independent.Version, out _));
        Assert.Equal(initialGeneration, catalog.Generation);

        batch.Commit();

        Assert.Same(originalContext, ContextOf(loader, previous));
        Assert.True(loader.TryGetContext(independent.Id, independent.Version, out _));
        Assert.False(loader.TryGetContext(replacement.Id, replacement.Version, out _));
        Assert.False(loader.TryGetContext(collidesWithFallback.Id, collidesWithFallback.Version, out _));
        Assert.Equal(initialGeneration + 1, catalog.Generation);
    }

    [Fact]
    public async Task StageOrdinaryGraphsAsync_WhenBothCandidateGraphsExposeTheSameAssembly_RejectsBeforeCreatingContexts()
    {
        var catalog = new HostIntegratedAssemblyResolutionCatalog();
        var loader = new PackageLoader(
            hostIntegratedResolutionCatalog: catalog,
            options: Options.Create(new LoadingOptions { DefaultLoadMode = PackageLoadMode.HostIntegrated }));
        var first = Install("plugin-a", "1.0.0", typeof(FixtureMarker).Assembly);
        var firstDependency = Install("dependency", "1.0.0", typeof(PackageLoaderStagedActivationTests).Assembly);
        var second = Install("plugin-b", "1.0.0", typeof(FixtureMarker).Assembly);

        using var batch = await loader.StageOrdinaryGraphsAsync([[first, firstDependency], [second]], Versions(first, firstDependency, second), [], CancellationToken.None);
        var failures = batch.Result.FailedByPackageId;

        Assert.Equal(["dependency", "plugin-a", "plugin-b"], failures.Keys.Order(StringComparer.Ordinal));
        Assert.All(failures.Values, message => Assert.Contains(typeof(FixtureMarker).Assembly.GetName().Name!, message));
        Assert.Empty(batch.StagedContexts);
        Assert.Equal(0, catalog.Generation);
        Assert.Empty(loader.Sessions);
    }

    private ResolvedPackage Install(string packageId, string version, Assembly assembly)
    {
        var dir = _temp.CreateSubdirectory(Path.Combine(packageId, version));
        File.Copy(assembly.Location, Path.Combine(dir.FullName, Path.GetFileName(assembly.Location)));
        return new(packageId, version, "feed", dir.FullName, DateTimeOffset.UtcNow);
    }

    private static IReadOnlyDictionary<string, string> Versions(params ResolvedPackage[] packages) =>
        packages.ToDictionary(static package => package.Id, static package => package.Version, StringComparer.OrdinalIgnoreCase);

    private static ActivePackage ToActive(ResolvedPackage package) =>
        new(package.Id, package.Version, package.FeedName, package.SourceName, package.InstallPath,
            package.InstalledAt, "test")
        {
            ArchiveHash = package.PackageContentHash
        };

    private static AssemblyLoadContext ContextOf(PackageLoader loader, ResolvedPackage package)
    {
        Assert.True(loader.TryGetContext(package.Id, package.Version, out var handle));
        return Assert.IsAssignableFrom<AssemblyLoadContext>(handle!.Context);
    }

    public void Dispose()
    {
        try
        {
            _temp.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
