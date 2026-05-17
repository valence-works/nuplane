using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Observability;
using Nuplane.Loading.Tests.Fixtures;

namespace Nuplane.Loading.Tests;

public sealed class LoadingCatalogTests
{
    [Fact]
    public async Task GetLoadStateAsync_WhenDisabled_ReportsDisabledPackages()
    {
        var catalog = CreateCatalog(
            new LoadingOptions { Enabled = false },
            new ActivePackagesSnapshot(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [new ActivePackage("pkg-a", "1.0.0", "feed", "source", "/packages/pkg-a", DateTimeOffset.UtcNow, "corr")],
                "read-1"),
            new PackageLoader(),
            new LoadingCatalogRefreshTracker());

        var snapshot = await catalog.GetLoadStateAsync(CancellationToken.None);

        Assert.Equal(PackageLoadStateAvailability.Disabled, snapshot.Availability);
        Assert.Equal(PackageLoadStatus.Disabled, snapshot.Packages[0].Status);
    }

    [Fact]
    public async Task GetLoadStateAsync_WhenNotRefreshed_ReportsStalePackages()
    {
        var catalog = CreateCatalog(
            new LoadingOptions { Enabled = true },
            new ActivePackagesSnapshot(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [new ActivePackage("pkg-a", "1.0.0", "feed", "source", "/packages/pkg-a", DateTimeOffset.UtcNow, "corr")],
                "read-2"),
            new PackageLoader(),
            new LoadingCatalogRefreshTracker());

        var snapshot = await catalog.GetLoadStateAsync(CancellationToken.None);

        Assert.Equal(PackageLoadStateAvailability.Stale, snapshot.Availability);
        Assert.Equal(PackageLoadStatus.Stale, snapshot.Packages[0].Status);
    }

    [Fact]
    public async Task GetLoadStateAsync_WhenLoadedAndFailedPackagesExist_ReportsStatusesAndAssemblyReferences()
    {
        var refreshTracker = new LoadingCatalogRefreshTracker();
        var loader = new PackageLoader();
        var good = CreateResolvedPackage("pkg-good", "1.0.0");
        var badDescriptor = new ActivePackage("pkg-bad", "1.0.0", "feed", "source", "/path/does/not/exist", DateTimeOffset.UtcNow, "corr-bad");
        var activeSnapshot = new ActivePackagesSnapshot(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [
                new ActivePackage(good.Id, good.Version, good.FeedName, good.SourceName, good.InstallPath, DateTimeOffset.UtcNow, "corr-good"),
                badDescriptor
            ],
            "read-3");

        await loader.EnsureLoadedAsync([good, new ResolvedPackage("pkg-bad", "1.0.0", "feed", "/path/does/not/exist", DateTimeOffset.UtcNow, "source")], [], CancellationToken.None);
        refreshTracker.MarkRefreshed("refresh-1");

        var catalog = CreateCatalog(
            new LoadingOptions { Enabled = true },
            activeSnapshot,
            loader,
            refreshTracker);

        var snapshot = await catalog.GetLoadStateAsync(CancellationToken.None);

        var loaded = Assert.Single(snapshot.Packages, x => x.PackageId == "pkg-good");
        Assert.Equal(PackageLoadStatus.Loaded, loaded.Status);
        Assert.NotEmpty(loaded.AssemblyReferences);

        var failed = Assert.Single(snapshot.Packages, x => x.PackageId == "pkg-bad");
        Assert.Equal(PackageLoadStatus.Failed, failed.Status);
        Assert.NotEmpty(failed.Diagnostics);
        var contribution = await new LoadingOperationalStateContributor(
            new StubActivePackageCatalog(activeSnapshot),
            loader,
            refreshTracker,
            Options.Create(new LoadingOptions { Enabled = true }))
            .ContributeAsync(CancellationToken.None);

        Assert.Contains("load-state-issues:1", contribution.DegradedReasons);
        Assert.Contains("load-state-divergence:1", contribution.DegradedReasons);
    }

    [Fact]
    public async Task GetLoadStateAsync_WhenPackageHasLoadModeDiagnostics_ProjectsExplanations()
    {
        var refreshTracker = new LoadingCatalogRefreshTracker();
        var root = CreateResolvedPackage("pkg-root", "1.0.0", typeof(FixtureMarker).Assembly);
        PackageMetadataTestSupport.WriteMetadata(root.InstallPath);
        var dependency = CreateResolvedPackage("pkg-dependency", "1.0.0", typeof(LoadingCatalogTests).Assembly);
        var loader = new PackageLoader(loadModeAdvisors: [new PackageMetadataLoadModeAdvisor(new PackageMetadataLoadModeReader())]);
        var activeSnapshot = new ActivePackagesSnapshot(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [
                new ActivePackage(root.Id, root.Version, root.FeedName, root.SourceName, root.InstallPath, DateTimeOffset.UtcNow, "corr-root"),
                new ActivePackage(dependency.Id, dependency.Version, dependency.FeedName, dependency.SourceName, dependency.InstallPath, DateTimeOffset.UtcNow, "corr-dependency")
            ],
            "read-load-mode-diagnostics");

        await loader.EnsureGraphLoadedAsync([[root, dependency]], [], CancellationToken.None);
        refreshTracker.MarkRefreshed("refresh-load-mode-diagnostics");
        var catalog = CreateCatalog(
            new LoadingOptions { Enabled = true },
            activeSnapshot,
            loader,
            refreshTracker);

        var snapshot = await catalog.GetLoadStateAsync(CancellationToken.None);

        var rootDescriptor = Assert.Single(snapshot.Packages, package => package.PackageId == "pkg-root");
        Assert.Contains(rootDescriptor.LoadModeDiagnostics ?? [], diagnostic =>
            diagnostic.ReasonCode == LoadModeReasonCodes.PackageMetadata
            && diagnostic.DeclaringPackageId == "pkg-root"
            && diagnostic.RequestedScope == LoadModeScopes.DependencyClosure);

        var dependencyDescriptor = Assert.Single(snapshot.Packages, package => package.PackageId == "pkg-dependency");
        Assert.Contains(dependencyDescriptor.LoadModeDiagnostics ?? [], diagnostic =>
            diagnostic.ReasonCode == LoadModeReasonCodes.DependencyClosure
            && diagnostic.EffectiveGraphLoadMode == PackageLoadMode.HostIntegrated);
    }

    [Fact]
    public async Task GetLoadStateAsync_WhenDecisionReasonVaries_ProjectsStableReasonCodes()
    {
        var suppressedPackage = CreateResolvedPackage("pkg-suppressed", "1.0.0");
        PackageMetadataTestSupport.WriteMetadata(suppressedPackage.InstallPath);
        var suppressedOptions = new LoadingOptions();
        suppressedOptions.PackageLoadModes.Add(new() { PackageId = "pkg-suppressed", LoadMode = PackageLoadMode.Collectible });

        var suppressed = await LoadAndReadPackageAsync(suppressedOptions, [suppressedPackage]);

        Assert.Contains(suppressed.LoadModeDiagnostics ?? [], diagnostic => diagnostic.ReasonCode == LoadModeReasonCodes.PackageOverride);
        Assert.Contains(suppressed.LoadModeDiagnostics ?? [], diagnostic => diagnostic.ReasonCode == LoadModeReasonCodes.MetadataSuppressed);

        var invalidPackage = CreateResolvedPackage("pkg-invalid", "1.0.0");
        File.WriteAllText(Path.Combine(invalidPackage.InstallPath, PackageMetadataLoadModeReader.MetadataFileName), "{");

        var invalid = await LoadAndReadPackageAsync(new LoadingOptions(), [invalidPackage]);

        Assert.Contains(invalid.LoadModeDiagnostics ?? [], diagnostic => diagnostic.ReasonCode == LoadModeReasonCodes.MetadataInvalid);
        Assert.Contains(invalid.LoadModeDiagnostics ?? [], diagnostic => diagnostic.ReasonCode == LoadModeReasonCodes.Default);

        var disabledPackage = CreateResolvedPackage("pkg-disabled", "1.0.0");
        PackageMetadataTestSupport.WriteMetadata(disabledPackage.InstallPath);

        var disabled = await LoadAndReadPackageAsync(
            new LoadingOptions { LoadModeSelectionPolicy = PackageLoadModeSelectionPolicy.ExplicitOnly },
            [disabledPackage]);

        Assert.Contains(disabled.LoadModeDiagnostics ?? [], diagnostic => diagnostic.ReasonCode == LoadModeReasonCodes.AdvisorsDisabled);

        var hostPackage = CreateResolvedPackage("pkg-conflict-host", "1.0.0", typeof(FixtureMarker).Assembly);
        PackageMetadataTestSupport.WriteMetadata(hostPackage.InstallPath, PackageLoadMode.HostIntegrated);
        var collectiblePackage = CreateResolvedPackage("pkg-conflict-collectible", "1.0.0", typeof(LoadingCatalogTests).Assembly);
        PackageMetadataTestSupport.WriteMetadata(collectiblePackage.InstallPath, PackageLoadMode.Collectible);

        var conflicted = await LoadAndReadPackageAsync(new LoadingOptions(), [hostPackage, collectiblePackage]);

        Assert.All(conflicted.LoadModeDiagnostics ?? [], diagnostic =>
            Assert.Contains(diagnostic.ReasonCode, new[]
            {
                LoadModeReasonCodes.PackageMetadata,
                LoadModeReasonCodes.DependencyClosure,
                LoadModeReasonCodes.MetadataConflict
            }));
        Assert.Contains(conflicted.LoadModeDiagnostics ?? [], diagnostic => diagnostic.ReasonCode == LoadModeReasonCodes.MetadataConflict);
    }

    private static LoadingCatalog CreateCatalog(
        LoadingOptions options,
        ActivePackagesSnapshot snapshot,
        PackageLoader loader,
        LoadingCatalogRefreshTracker refreshTracker)
    {
        return new LoadingCatalog(
            new StubActivePackageCatalog(snapshot),
            loader,
            new AssemblyScanCandidateProjector(loader),
            refreshTracker,
            Options.Create(options),
            new ReconciliationLogger(),
            new ReconciliationMetrics(new ReconciliationTelemetry()));
    }

    private static async Task<PackageLoadState> LoadAndReadPackageAsync(
        LoadingOptions options,
        IReadOnlyList<ResolvedPackage> packages)
    {
        var loader = new PackageLoader(
            loadModeAdvisors: [new PackageMetadataLoadModeAdvisor(new PackageMetadataLoadModeReader())],
            options: Options.Create(options));
        var refreshTracker = new LoadingCatalogRefreshTracker();
        var activeSnapshot = new ActivePackagesSnapshot(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            packages
                .Select(package => new ActivePackage(package.Id, package.Version, package.FeedName, package.SourceName, package.InstallPath, DateTimeOffset.UtcNow, $"corr-{package.Id}"))
                .ToArray(),
            $"read-{Guid.NewGuid():N}");

        await loader.EnsureGraphLoadedAsync([packages], [], CancellationToken.None);
        refreshTracker.MarkRefreshed($"refresh-{Guid.NewGuid():N}");

        var catalog = CreateCatalog(new LoadingOptions { Enabled = true }, activeSnapshot, loader, refreshTracker);
        var snapshot = await catalog.GetLoadStateAsync(CancellationToken.None);
        return snapshot.Packages[0];
    }

    private static ResolvedPackage CreateResolvedPackage(string id, string version) =>
        CreateResolvedPackage(id, version, typeof(PackageLoader).Assembly);

    private static ResolvedPackage CreateResolvedPackage(string id, string version, System.Reflection.Assembly sourceAssembly)
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-loading-catalog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var targetAssembly = Path.Combine(root, $"{id}.dll");
        File.Copy(sourceAssembly.Location, targetAssembly, overwrite: true);

        return new ResolvedPackage(id, version, "feed-a", root, DateTimeOffset.UtcNow, "source-a");
    }

    private sealed class StubActivePackageCatalog(ActivePackagesSnapshot snapshot) : IActivePackageCatalog
    {
        public Task<ActivePackagesSnapshot> GetActivePackagesAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }
}
