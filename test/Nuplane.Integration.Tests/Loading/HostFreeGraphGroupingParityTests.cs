using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Loading;
using Nuplane.Loading.Hosting.Builder;
using Nuplane.Operational;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Loading;

/// <summary>
/// Pins the grouping half of "the host-free load behaves identically to a real host's host-integrated
/// load for the same package set". Graph membership decides the load-context key and which packages can
/// see each other's assemblies, and a store can hold several overlapping active graph records at once —
/// a record from an earlier reconcile survives until a newer graph with the same root set replaces it.
/// <para>
/// The state here is built through the real activation mapper, the real store and the real serializer:
/// two activations with different roots that share a dependency, which is exactly the shape that leaves
/// two overlapping <see cref="GraphActivationStatus.Active"/> records behind and gives the two packages'
/// descriptors different graph generations.
/// </para>
/// </summary>
public sealed class HostFreeGraphGroupingParityTests : IDisposable
{
    private const string Version = "1.0.0";
    private const string FirstCorrelationId = "corr-activation-1";
    private const string SecondCorrelationId = "corr-activation-2";

    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("nuplane-host-free-grouping-");

    public void Dispose()
    {
        try
        {
            // Files loaded into non-collectible contexts can stay locked for the process lifetime.
            _tempDir.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task LoadActivePackagesAsync_FromStateWithOverlappingActiveGraphRecords_GroupsExactlyAsAComposedHostDoes()
    {
        // Arrange
        var fixture = await CreateOverlappingGraphStateAsync(
            "grouping-parity",
            typeof(Nuplane.Admin.NuplaneAdminServiceCollectionExtensions).Assembly,
            typeof(PackageLoadState).Assembly,
            typeof(ActivePackage).Assembly);

        // The premise of this test: the persisted state really does hold two overlapping active graph
        // records, and the shared dependency's descriptor carries only the second activation's generation.
        var state = await NuplaneStore.ReadStateAsync(fixture.StateFilePath);
        var activeGraphs = state.ActiveGraphsByIdNormalized.Values
            .Where(static graph => graph.Status == GraphActivationStatus.Active)
            .ToArray();
        Assert.Equal(2, activeGraphs.Length);
        Assert.All(activeGraphs, graph => Assert.Contains(fixture.DependencyPackageId, graph.NodePackageIds));
        Assert.NotEqual(
            state.ActivePackageDescriptorsByIdNormalized[fixture.FirstRootPackageId].GraphGenerationId,
            state.ActivePackageDescriptorsByIdNormalized[fixture.SecondRootPackageId].GraphGenerationId);

        // Act: the host-free load runs first, because a graph a composed host has already loaded into a
        // non-collectible context is deliberately refused rather than loaded a second time.
        var hostFree = await NuplaneHostIntegratedLoader.LoadActivePackagesAsync(fixture.StateFilePath);

        await using var host = CreateHost(fixture.StateFilePath);
        await ReconcileAsync(host, fixture.ResolvedPackages);
        var hostState = await host.GetRequiredService<IPackageLoadStateCatalog>().GetLoadStateAsync(CancellationToken.None);

        // Assert
        Assert.Empty(hostFree.FailedByPackageId);
        Assert.Equal(3, hostFree.Packages.Count);
        Assert.Equal(3, hostState.Packages.Count);

        // The packages the two overlapping records activated load as ONE graph, because they share a
        // package and were therefore activated together. Grouping by graph generation alone would have
        // split them — see the companion test below.
        Assert.Single(GraphKeysOf(hostFree.Packages));
        Assert.Equal(GraphKeysOf(hostState.Packages), GraphKeysOf(hostFree.Packages));

        foreach (var expected in hostState.Packages)
        {
            var actual = Assert.Single(hostFree.Packages, package => package.PackageId == expected.PackageId);

            Assert.Equal(PackageLoadStatus.Loaded, actual.Status);
            Assert.Equal(expected.Status, actual.Status);
            Assert.Equal(expected.LoadMode, actual.LoadMode);
            Assert.Equal(expected.FrameworkIntegrationSafe, actual.FrameworkIntegrationSafe);
            Assert.Equal(expected.InstallPath, actual.InstallPath);
            Assert.Equal(expected.AssemblyReferences, actual.AssemblyReferences);
            Assert.Equal(expected.LoadModeDiagnostics, actual.LoadModeDiagnostics);
        }
    }

    [Fact]
    public async Task LoadActivePackagesAsync_WithTheActivePackageListOnly_GroupsByGraphGenerationInstead()
    {
        // Arrange: the same state shape, with its own package AND assembly identities, because the
        // host-integrated resolution catalog this process shares refuses two packages that expose the
        // same assembly — which is exactly why each test must bring its own assemblies.
        var fixture = await CreateOverlappingGraphStateAsync(
            "grouping-list",
            typeof(Nuplane.Admin.Api.NuplaneAdminEndpointExtensions).Assembly,
            typeof(Nuplane.Loading.Api.NuplaneLoadStateEndpointExtensions).Assembly,
            typeof(Nuplane.Sources.Directory.DirectorySourceOptions).Assembly);
        var activePackages = await NuplaneStore.ReadActivePackagesAsync(fixture.StateFilePath);

        // Act
        var result = await NuplaneHostIntegratedLoader.LoadActivePackagesAsync(activePackages);

        // Assert: an ActivePackage carries its graph generation but not the store's graph records, so
        // this overload splits what the store activated together — the first root loads on its own,
        // while the second root and the shared dependency, which share a generation, load together.
        // This is the documented limit of the list overload and the reason the from-state overload
        // exists; nothing here is a failure, it is simply a different, smaller graph.
        Assert.Empty(result.FailedByPackageId);
        Assert.Equal(2, GraphKeysOf(result.Packages).Count);
        Assert.Equal(
            GraphKeyOf(result.Packages, fixture.SecondRootPackageId),
            GraphKeyOf(result.Packages, fixture.DependencyPackageId));
        Assert.NotEqual(
            GraphKeyOf(result.Packages, fixture.FirstRootPackageId),
            GraphKeyOf(result.Packages, fixture.DependencyPackageId));
    }

    private static IReadOnlyCollection<string> GraphKeysOf(IEnumerable<PackageLoadState> packages) =>
        packages
            .SelectMany(static package => package.LoadModeDiagnostics ?? [])
            .Select(static diagnostic => diagnostic.GraphKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string GraphKeyOf(IEnumerable<PackageLoadState> packages, string packageId) =>
        Assert.Single(GraphKeysOf(packages.Where(package => package.PackageId == packageId)));

    private static ServiceProvider CreateHost(string stateFilePath)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(nuplane =>
        {
            nuplane.WithStateFile(stateFilePath);
            nuplane.AutoloadPackages(loading => loading.WithDefaultLoadMode(PackageLoadMode.HostIntegrated));
        });

        return services.BuildServiceProvider();
    }

    // Drives the exact call the reconciliation pipeline and last-known-good startup recovery make.
    private static Task ReconcileAsync(IServiceProvider host, IReadOnlyList<ResolvedPackage> packages) =>
        host.GetRequiredService<IObserverEventDispatcher>().PublishReconciledAsync(
            new PackageChangeSet([], [], [], SecondCorrelationId, DateTimeOffset.UtcNow),
            packages,
            CancellationToken.None);

    /// <summary>
    /// Persists the state two activations leave behind: the first activates root one with a shared
    /// dependency, the second activates root two with the same dependency. The activation records and
    /// package descriptors are produced by the real <see cref="ActivePackageCatalogMapper"/>, which is
    /// what decides that the first activation's record survives the second, and persisted through the
    /// real store, so the state file is the one a host would have written.
    /// </summary>
    private async Task<OverlappingGraphFixture> CreateOverlappingGraphStateAsync(
        string scenario,
        Assembly firstRootAssembly,
        Assembly secondRootAssembly,
        Assembly dependencyAssembly)
    {
        var firstRoot = CreatePackage($"{scenario}-root-one", firstRootAssembly);
        var secondRoot = CreatePackage($"{scenario}-root-two", secondRootAssembly);
        var dependency = CreatePackage($"{scenario}-dependency", dependencyAssembly);

        var firstGraph = CreateGraph($"{scenario}-graph-one", $"{scenario}-generation-one", firstRoot, dependency);
        var secondGraph = CreateGraph($"{scenario}-graph-two", $"{scenario}-generation-two", secondRoot, dependency);

        var stateFilePath = Path.Combine(_tempDir.FullName, $"{scenario}-store-state.json");
        await using var provider = CreateHost(stateFilePath);
        var storeRegistry = provider.GetRequiredService<IStoreRegistry>();

        await PersistActivationAsync(storeRegistry, firstGraph, [firstRoot, dependency], FirstCorrelationId);
        await PersistActivationAsync(storeRegistry, secondGraph, [secondRoot, dependency], SecondCorrelationId);

        return new(
            stateFilePath,
            firstRoot.Id,
            secondRoot.Id,
            dependency.Id,
            [firstRoot, secondRoot, dependency]);
    }

    private static async Task PersistActivationAsync(
        IStoreRegistry storeRegistry,
        ResolvedPackageGraph graph,
        IReadOnlyList<ResolvedPackage> appliedPackages,
        string correlationId)
    {
        var currentState = await storeRegistry.GetStateAsync(CancellationToken.None);
        var activatedAtUtc = DateTimeOffset.UtcNow;
        var nextActiveVersions = new Dictionary<string, string>(currentState.ActiveVersionById, StringComparer.OrdinalIgnoreCase);
        foreach (var package in appliedPackages)
        {
            nextActiveVersions[package.Id] = package.Version;
        }

        var changeSet = new PackageChangeSet(appliedPackages, [], [], correlationId, activatedAtUtc);

        await storeRegistry.PersistActiveVersionsAsync(
            nextActiveVersions,
            appliedPackages.ToDictionary(static package => package.Id, static package => package.Version, StringComparer.OrdinalIgnoreCase),
            correlationId,
            CancellationToken.None,
            ActivePackageCatalogMapper.BuildNextDescriptors(
                currentState,
                nextActiveVersions,
                appliedPackages,
                changeSet,
                correlationId,
                activatedAtUtc,
                [graph]),
            ActivePackageCatalogMapper.BuildActiveGraphRecords(
                currentState,
                [graph],
                nextActiveVersions,
                correlationId,
                activatedAtUtc));
    }

    private static ResolvedPackageGraph CreateGraph(
        string graphId,
        string generationId,
        ResolvedPackage root,
        ResolvedPackage dependency)
    {
        var rootNode = CreateNode(root, PackageNodeRole.Root);
        var dependencyNode = CreateNode(dependency, PackageNodeRole.Dependency);

        return new(
            graphId,
            generationId,
            "net10.0",
            [rootNode],
            [rootNode, dependencyNode],
            [new(root.Id, root.Version, dependency.Id, dependency.Version, dependency.Version, "net10.0", false)],
            [],
            DateTimeOffset.UtcNow);
    }

    private static ResolvedPackageNode CreateNode(ResolvedPackage package, PackageNodeRole role) =>
        new(
            package.Id,
            package.Version,
            role,
            package.InstallPath,
            PackageSourceKind.LocalDirectory,
            package.SourceName,
            null,
            [],
            [],
            []);

    /// <summary>
    /// Creates an install directory holding one distinct assembly. Host-integrated loads are
    /// process-global, so every package in these tests must carry an assembly identity no other package
    /// in the process exposes.
    /// </summary>
    private ResolvedPackage CreatePackage(string packageId, Assembly sourceAssembly)
    {
        var installDirectory = _tempDir.CreateSubdirectory(packageId);
        File.Copy(sourceAssembly.Location, Path.Combine(installDirectory.FullName, Path.GetFileName(sourceAssembly.Location)));

        return new(packageId, Version, "feed-a", installDirectory.FullName, DateTimeOffset.UtcNow, "source-a");
    }

    private sealed record OverlappingGraphFixture(
        string StateFilePath,
        string FirstRootPackageId,
        string SecondRootPackageId,
        string DependencyPackageId,
        IReadOnlyList<ResolvedPackage> ResolvedPackages);
}
