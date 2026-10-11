using Nuplane.Abstractions;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.LockFile;
using Nuplane.Reconciliation.Models;
using Nuplane.Runtime.Tests.TestSupport;
using Nuplane.Store.Activation;
using Nuplane.Store.Transactions;
using Microsoft.Extensions.Options;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class PackageApplyExecutorTests : IDisposable
{
    private static readonly string HashA = $"sha512:{Convert.ToBase64String(new byte[64])}";
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"nuplane-apply-executor-{Guid.NewGuid():N}");

    [Fact]
    public async Task ResolveAsync_EnforceEntry_ConstrainsRootBeforeAcquisition()
    {
        var resolved = CreateInstalledPackage("Root.A", "2.0.0") with
        {
            FeedName = "locked-feed",
            PackageContentHash = HashA
        };
        var resolver = new RecordingResolver(resolved);
        var recorder = new RecordingFailureRecorder();
        var coordinator = await CreateLockCoordinatorAsync(
            LockFileMode.Enforce,
            [new("Root.A", "2.0.0", "locked-feed", HashA, DateTimeOffset.UtcNow)]);
        var snapshot = await coordinator.CaptureAsync(CancellationToken.None);
        var sut = new PackageApplyExecutor(
            resolver,
            new PackageTransactionCoordinator(new AtomicPointerSwitcher(), recorder),
            new PassthroughRetryPolicy(),
            recorder);

        var result = await sut.ResolveAsync(
            [new PackageRequest("Root.A", "[1.0.0, 3.0.0)", "live-feed", PackageUpdatePolicy.Range, "source")],
            "corr-lock",
            coordinator,
            snapshot,
            CancellationToken.None);

        var request = Assert.Single(resolver.Requests);
        Assert.Equal("2.0.0", request.VersionRange);
        Assert.Equal("locked-feed", request.FeedName);
        Assert.Equal(PackageUpdatePolicy.Exact, request.UpdatePolicy);
        Assert.Empty(result.FailedPackageIds);
        Assert.Single(result.ResolvedPackages);
    }

    [Fact]
    public async Task ExecuteTransactionsAsync_ResolvedGraphSelectionSnapshot_PreservesSuccessfulApplicationAssociation()
    {
        var scenario = await CreateGraphApplyScenarioAsync();
        var resolvedGraph = Assert.Single(scenario.Resolution.ResolvedGraphs);
        var resolvedSelection = Assert.Single(scenario.Resolution.GraphSelections);
        Assert.NotSame(resolvedGraph, resolvedSelection.Graph);

        var applied = await scenario.Executor.ExecuteTransactionsAsync(
            scenario.Resolution,
            "corr-graph",
            CancellationToken.None);

        var successfulSelection = Assert.Single(applied.SuccessfulGraphSelections);
        Assert.Equal([scenario.Package], applied.AppliedPackages);
        Assert.Equal([scenario.Request], successfulSelection.RootRequests);
        Assert.Equal(resolvedGraph.GraphId, successfulSelection.Graph.GraphId);
        Assert.Equal(resolvedGraph.GenerationId, successfulSelection.Graph.GenerationId);
        Assert.Equal(scenario.Package.Version, scenario.PointerSwitcher.GetCurrentVersion(scenario.Package.Id));
        Assert.Empty(scenario.FailureRecorder.Records);
    }

    [Fact]
    public async Task ExecuteTransactionsAsync_InconsistentExplicitGraphSelection_RefusesBeforeApplicationMutation()
    {
        var scenario = await CreateGraphApplyScenarioAsync();
        var selection = Assert.Single(scenario.Resolution.GraphSelections);
        var changedPath = selection.Graph.Nodes[0] with
        {
            InstallPath = Path.Combine(_tempRoot, "different-install-path")
        };
        var changedPackage = selection.Packages[0] with { InstallPath = changedPath.InstallPath! };
        var pathMismatchGraph = selection.Graph with
        {
            Nodes = Array.AsReadOnly(selection.Graph.Nodes.Select((node, index) => index == 0 ? changedPath : node).ToArray())
        };
        var invalidResolutions = new[]
        {
            scenario.Resolution with
            {
                GraphSelections =
                [
                    new ResolvedPackageGraphSelection(
                        selection.Graph with { GenerationId = "different-generation" },
                        selection.RootRequests,
                        selection.Packages)
                ]
            },
            scenario.Resolution with
            {
                GraphSelections =
                [new ResolvedPackageGraphSelection(pathMismatchGraph, selection.RootRequests, [changedPackage])]
            },
            scenario.Resolution with { GraphSelections = [selection, selection] }
        };

        foreach (var inconsistent in invalidResolutions)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Executor.ExecuteTransactionsAsync(
                inconsistent,
                "corr-graph",
                CancellationToken.None));

            Assert.Null(scenario.PointerSwitcher.GetCurrentVersion(scenario.Package.Id));
            Assert.Empty(scenario.FailureRecorder.Records);
        }
    }

    [Fact]
    public async Task ExecuteTransactionsAsync_FlatResolvedPackagePathMismatch_RefusesBeforeApplicationMutation()
    {
        var scenario = await CreateGraphApplyScenarioAsync();
        var inconsistent = scenario.Resolution with
        {
            ResolvedPackages = [scenario.Package with { InstallPath = Path.Combine(_tempRoot, "different-flat-path") }]
        };

        Assert.Same(scenario.Resolution.ResolvedGraphs, inconsistent.ResolvedGraphs);
        Assert.Same(scenario.Resolution.GraphSelections, inconsistent.GraphSelections);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Executor.ExecuteTransactionsAsync(
            inconsistent,
            "corr-flat-path",
            CancellationToken.None));

        Assert.Null(scenario.PointerSwitcher.GetCurrentVersion(scenario.Package.Id));
        Assert.Empty(scenario.FailureRecorder.Records);
    }

    [Fact]
    public async Task ExecuteTransactionsAsync_ExplicitGraphSelectionRequiresUniqueCompleteFlatPackageProjection()
    {
        var scenario = await CreateGraphApplyScenarioAsync();
        var invalidResolutions = new[]
        {
            scenario.Resolution with { ResolvedPackages = [] },
            scenario.Resolution with { ResolvedPackages = [scenario.Package, scenario.Package] }
        };

        foreach (var inconsistent in invalidResolutions)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Executor.ExecuteTransactionsAsync(
                inconsistent,
                "corr-flat-projection",
                CancellationToken.None));

            Assert.Null(scenario.PointerSwitcher.GetCurrentVersion(scenario.Package.Id));
            Assert.Empty(scenario.FailureRecorder.Records);
        }
    }

    [Fact]
    public async Task ExecuteTransactionsAsync_GraphWithoutExplicitSelection_PreservesLegacyApplication()
    {
        var scenario = await CreateGraphApplyScenarioAsync();
        var legacyResolution = scenario.Resolution with { GraphSelections = [] };

        var applied = await scenario.Executor.ExecuteTransactionsAsync(
            legacyResolution,
            "corr-legacy",
            CancellationToken.None);

        Assert.Equal([scenario.Package], applied.AppliedPackages);
        Assert.Empty(applied.SuccessfulGraphSelections);
        Assert.Equal(scenario.Package.Version, scenario.PointerSwitcher.GetCurrentVersion(scenario.Package.Id));
        Assert.Empty(scenario.FailureRecorder.Records);
    }

    [Fact]
    public async Task ResolveAsync_StrictDependencyMissingEntry_DoesNotAcquireDependencyOrProduceGraph()
    {
        var root = CreateInstalledPackage("Root.A", "1.0.0", "Dependency.A", "[1.0.0]") with
        {
            PackageContentHash = HashA
        };
        var resolver = new RecordingResolver(root);
        var recorder = new RecordingFailureRecorder();
        var coordinator = await CreateLockCoordinatorAsync(
            LockFileMode.Strict,
            [new("Root.A", "1.0.0", "test-feed", HashA, DateTimeOffset.UtcNow)]);
        var snapshot = await coordinator.CaptureAsync(CancellationToken.None);
        var sut = new PackageApplyExecutor(
            resolver,
            new PackageTransactionCoordinator(new AtomicPointerSwitcher(), recorder),
            new PassthroughRetryPolicy(),
            recorder);

        var result = await sut.ResolveAsync(
            [new PackageRequest("Root.A", "1.0.0", "test-feed", PackageUpdatePolicy.Exact, "source")],
            "corr-lock",
            coordinator,
            snapshot,
            CancellationToken.None);

        Assert.Single(resolver.Requests, static request => request.Id == "Root.A");
        Assert.DoesNotContain(resolver.Requests, static request => request.Id == "Dependency.A");
        Assert.Equal(["Root.A"], result.FailedPackageIds);
        Assert.Empty(result.ResolvedPackages);
        Assert.Empty(result.ResolvedGraphs);
        Assert.Contains(recorder.Records, static record => record.PackageId == "Root.A" && record.Stage == "lock");
    }

    [Fact]
    public async Task ResolveAsync_WhenConflictAndIndependentResolutionFailure_RecordsConflictOnlyForConflictRoots()
    {
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
        {
            ["Root.A"] = CreateInstalledPackage("Root.A", "1.0.0", "Shared.Dependency", "[1.0.0]"),
            ["Root.B"] = CreateInstalledPackage("Root.B", "1.0.0", "Shared.Dependency", "[2.0.0]"),
            ["Shared.Dependency:1.0.0"] = CreateInstalledPackage("Shared.Dependency", "1.0.0"),
            ["Shared.Dependency:2.0.0"] = CreateInstalledPackage("Shared.Dependency", "2.0.0")
        });
        var recorder = new RecordingFailureRecorder();
        var sut = new PackageApplyExecutor(
            resolver,
            new PackageTransactionCoordinator(new AtomicPointerSwitcher(), recorder),
            new PassthroughRetryPolicy(),
            recorder);

        await sut.ResolveAsync(
            [
                new PackageRequest("Root.A", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source"),
                new PackageRequest("Root.B", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source"),
                new PackageRequest("Missing.Root", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source")
            ],
            "corr-1",
            CancellationToken.None);

        Assert.Contains(recorder.Records, static record => record.PackageId == "Missing.Root" && record.Stage == "resolve");
        Assert.DoesNotContain(recorder.Records, static record => record.PackageId == "Missing.Root" && record.Stage == "resolve-graph-conflict");
        Assert.Contains(recorder.Records, static record => record.PackageId == "Root.A" && record.Stage == "resolve-graph-conflict");
        Assert.Contains(recorder.Records, static record => record.PackageId == "Root.B" && record.Stage == "resolve-graph-conflict");
    }

    [Fact]
    public async Task ResolveAsync_WhenCompatibleBareDependencyBaseline_DoesNotRecordGraphConflict()
    {
        var resolver = new VersionRangePackageResolver(new Dictionary<string, IReadOnlyList<ResolvedPackage>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Root.Current"] = [CreateInstalledPackage("Root.Current", "1.0.0", "Shared.Dependency", "[10.0.3]")],
            ["Root.Baseline"] = [CreateInstalledPackage("Root.Baseline", "1.0.0", "Shared.Dependency", "8.0.2")],
            ["Shared.Dependency"] = [CreateInstalledPackage("Shared.Dependency", "10.0.3")]
        });
        var recorder = new RecordingFailureRecorder();
        var sut = new PackageApplyExecutor(
            resolver,
            new PackageTransactionCoordinator(new AtomicPointerSwitcher(), recorder),
            new PassthroughRetryPolicy(),
            recorder);

        var result = await sut.ResolveAsync(
            [
                new PackageRequest("Root.Current", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source"),
                new PackageRequest("Root.Baseline", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source")
            ],
            "corr-1",
            CancellationToken.None);

        Assert.Empty(recorder.Records);
        Assert.Contains(result.ResolvedPackages, static package => package.Id == "Shared.Dependency" && package.Version == "10.0.3");
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Equal(["Root.Baseline", "Root.Current"], graph.Roots.Select(static root => root.PackageId).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(graph.Nodes, static node => node.PackageId == "Shared.Dependency" && node.Version == "10.0.3");
    }

    [Fact]
    public async Task ResolveAsync_WhenRootDependsOnDeclaredHostPackageAtUnsatisfiedVersion_RefusesThatRootAndAppliesTheOthers()
    {
        // Microsoft.Extensions.Options is in this test host's own *.deps.json at a version far above
        // 1.0.0, so an exact [1.0.0] dependency on it, once declared host-provided, is one the host
        // says it supplies but cannot satisfy.
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
        {
            ["Root.Refused"] = CreateInstalledPackage("Root.Refused", "1.0.0", "Microsoft.Extensions.Options", "[1.0.0]"),
            ["Root.Applied"] = CreateInstalledPackage("Root.Applied", "1.0.0")
        });
        var recorder = new RecordingFailureRecorder();
        var logger = new RecordingReconciliationLogger();
        var sut = new PackageApplyExecutor(
            resolver,
            new PackageTransactionCoordinator(new AtomicPointerSwitcher(), recorder),
            new PassthroughRetryPolicy(),
            recorder,
            reconciliationLogger: logger,
            hostProvidedPackagesOptions: HostProvidedPackagesTestSupport.WithEntries("Microsoft.Extensions.Options"));

        var result = await sut.ResolveAsync(
            [
                new PackageRequest("Root.Refused", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source"),
                new PackageRequest("Root.Applied", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source")
            ],
            "corr-1",
            CancellationToken.None);

        var record = Assert.Single(recorder.Records);
        Assert.Equal("Root.Refused", record.PackageId);
        Assert.Equal(PackageDependencyGraphResolver.HostVersionUnsatisfiedStage, record.Stage);
        Assert.Equal("corr-1", record.CorrelationId);
        Assert.Contains("'Microsoft.Extensions.Options [1.0.0]'", record.Message);
        Assert.Equal(["Root.Refused"], result.FailedPackageIds);
        Assert.Equal(["Root.Applied"], result.ResolvedPackages.Select(static package => package.Id));
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Equal(["Root.Applied"], graph.Roots.Select(static root => root.PackageId));
        Assert.Single(logger.HostVersionRefused, static refused => refused.PackageId == "Root.Refused");
    }

    [Fact]
    public async Task ResolveAsync_WhenDeclaredHostPackageVersionIsUnknown_AppliesRootAndLogsTheUncheckedDependency()
    {
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
        {
            ["Root.A"] = CreateInstalledPackage("Root.A", "1.0.0", "Acme.Contracts", "[2.0.0, 3.0.0)")
        });
        var recorder = new RecordingFailureRecorder();
        var logger = new RecordingReconciliationLogger();
        var sut = new PackageApplyExecutor(
            resolver,
            new PackageTransactionCoordinator(new AtomicPointerSwitcher(), recorder),
            new PassthroughRetryPolicy(),
            recorder,
            reconciliationLogger: logger,
            hostProvidedPackagesOptions: HostProvidedPackagesTestSupport.WithEntries("Acme.Contracts"));

        var result = await sut.ResolveAsync(
            [new PackageRequest("Root.A", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source")],
            "corr-1",
            CancellationToken.None);

        Assert.Empty(recorder.Records);
        Assert.Empty(result.FailedPackageIds);
        Assert.Equal(["Root.A"], result.ResolvedPackages.Select(static package => package.Id));
        Assert.Equal([("Root.A", "Acme.Contracts", "[2.0.0, 3.0.0)")], logger.HostVersionUnknown);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private async Task<GraphApplyScenario> CreateGraphApplyScenarioAsync()
    {
        var package = CreateInstalledPackage("Root.Graph", "1.0.0");
        var request = new PackageRequest("Root.Graph", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source");
        var resolver = new RecordingResolver(package);
        var failureRecorder = new RecordingFailureRecorder();
        var pointerSwitcher = new AtomicPointerSwitcher();
        var executor = new PackageApplyExecutor(
            resolver,
            new PackageTransactionCoordinator(pointerSwitcher, failureRecorder),
            new PassthroughRetryPolicy(),
            failureRecorder);
        var resolution = await executor.ResolveAsync([request], "corr-graph", CancellationToken.None);

        return new(executor, pointerSwitcher, failureRecorder, package, request, resolution);
    }

    private ResolvedPackage CreateInstalledPackage(
        string packageId,
        string version,
        string? dependencyId = null,
        string? dependencyVersionRange = null)
    {
        var installPath = Path.Combine(_tempRoot, packageId, version);
        Directory.CreateDirectory(installPath);
        File.WriteAllText(
            Path.Combine(installPath, $"{packageId}.nuspec"),
            CreateNuspec(packageId, version, dependencyId, dependencyVersionRange));
        return new(packageId, version, "test-feed", installPath, DateTimeOffset.UtcNow, "test-source");
    }

    private async Task<LockFileCoordinator> CreateLockCoordinatorAsync(
        LockFileMode mode,
        IReadOnlyList<PackageLockEntry> entries)
    {
        Directory.CreateDirectory(_tempRoot);
        var path = Path.Combine(_tempRoot, $"{Guid.NewGuid():N}.lock.json");
        var options = new LockFileOptions { Mode = mode, Path = path };
        var wrapped = new OptionsWrapper<LockFileOptions>(options);
        var store = new LockFileStore(wrapped);
        await store.WriteAsync(new("2.0", DateTimeOffset.UtcNow, entries), CancellationToken.None);
        return new(store, wrapped);
    }

    private static string CreateNuspec(
        string packageId,
        string version,
        string? dependencyId,
        string? dependencyVersionRange) =>
        $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>{{packageId}}</id>
            <version>{{version}}</version>
            <authors>test</authors>
            <description>Test package</description>
            {{CreateDependencies(dependencyId, dependencyVersionRange)}}
          </metadata>
        </package>
        """;

    private static string CreateDependencies(string? dependencyId, string? dependencyVersionRange) =>
        string.IsNullOrWhiteSpace(dependencyId) || string.IsNullOrWhiteSpace(dependencyVersionRange)
            ? string.Empty
            : $"<dependencies><dependency id=\"{dependencyId}\" version=\"{dependencyVersionRange}\" /></dependencies>";

    private sealed class StubPackageResolver(IReadOnlyDictionary<string, ResolvedPackage> packages) : IPackageResolver
    {
        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
        {
            var key = request.Id.StartsWith("Shared.Dependency", StringComparison.OrdinalIgnoreCase)
                ? $"{request.Id}:{request.VersionRange.Trim('[', ']')}"
                : request.Id;

            return packages.TryGetValue(key, out var package)
                ? Task.FromResult(package)
                : Task.FromException<ResolvedPackage>(new InvalidOperationException($"Package '{request.Id}' was not configured."));
        }
    }

    private sealed class RecordingResolver(ResolvedPackage package) : IPackageResolver
    {
        public List<PackageRequest> Requests { get; } = [];

        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(package);
        }
    }

    private sealed class PassthroughRetryPolicy : IReconciliationRetryPolicy
    {
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) => operation(cancellationToken);
    }

    private sealed record GraphApplyScenario(
        PackageApplyExecutor Executor,
        AtomicPointerSwitcher PointerSwitcher,
        RecordingFailureRecorder FailureRecorder,
        ResolvedPackage Package,
        PackageRequest Request,
        PackageResolutionResult Resolution);
}
