using Nuplane.Abstractions;
using Nuplane.Reconciliation;
using Nuplane.Runtime.Tests.TestSupport;
using Nuplane.Store.Activation;
using Nuplane.Store.Transactions;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class PackageApplyExecutorTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"nuplane-apply-executor-{Guid.NewGuid():N}");

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

    private sealed class PassthroughRetryPolicy : IReconciliationRetryPolicy
    {
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) => operation(cancellationToken);
    }
}
