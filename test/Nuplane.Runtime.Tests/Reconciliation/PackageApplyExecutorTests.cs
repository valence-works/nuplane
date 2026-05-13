using Nuplane.Abstractions;
using Nuplane.Reconciliation;
using Nuplane.Runtime.Tests.TestSupport;
using Nuplane.Store.Activation;
using Nuplane.Store.State;
using Nuplane.Store.Transactions;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class PackageApplyExecutorTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), $"nuplane-apply-executor-{Guid.NewGuid():N}");

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

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private ResolvedPackage CreateInstalledPackage(
        string packageId,
        string version,
        string? dependencyId = null,
        string? dependencyVersionRange = null)
    {
        var installPath = Path.Combine(tempRoot, packageId, version);
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

    private sealed class RecordingFailureRecorder : IFailureRecorder
    {
        public List<FailureRecord> Records { get; } = [];

        public Task RecordAsync(string packageId, string stage, string message, string correlationId, CancellationToken cancellationToken)
        {
            Records.Add(new(packageId, stage, message, DateTimeOffset.UtcNow, correlationId));
            return Task.CompletedTask;
        }
    }

    private sealed class PassthroughRetryPolicy : IReconciliationRetryPolicy
    {
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) => operation(cancellationToken);
    }
}
