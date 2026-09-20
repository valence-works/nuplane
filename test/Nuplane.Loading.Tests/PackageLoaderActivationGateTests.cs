using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Loading.Tests.Fixtures;

namespace Nuplane.Loading.Tests;

/// <summary>
/// Verifies that <see cref="IPackageActivationGate"/> decisions are honored by <see cref="PackageLoader"/>:
/// a block prevents the graph from loading before any load context exists, fails closed on gate faults,
/// and leaves everything else exactly as it is without a gate.
/// </summary>
public sealed class PackageLoaderActivationGateTests : IDisposable
{
    private const string BlockReason = "module schema is behind the package.";

    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("nuplane-activation-gate-test-");
    private readonly HostIntegratedAssemblyResolutionCatalog _resolutionCatalog = new();
    private readonly LoadingOptions _loadingOptions = new();

    public void Dispose()
    {
        try
        {
            _tempDir.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_WithNoGateRegistered_LoadsGraphUnchanged()
    {
        var loader = CreateLoader();

        var result = await loader.EnsureGraphLoadedAsync([CreateGraph("pkg-a", "pkg-b")], [], CancellationToken.None);

        Assert.Empty(result.FailedByPackageId);
        Assert.Equal(2, result.Loaded.Count);
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_WhenGateAllows_LoadsGraphAndReceivesGraphContext()
    {
        var gate = Allowing();
        var loader = CreateLoader(gate);
        var graph = CreateGraph("pkg-a", "pkg-b");

        var result = await loader.EnsureGraphLoadedAsync([graph], [], CancellationToken.None);

        Assert.Empty(result.FailedByPackageId);
        Assert.Equal(2, result.Loaded.Count);
        var context = Assert.Single(gate.Invocations);
        Assert.StartsWith("graph:", context.GraphKey, StringComparison.Ordinal);
        Assert.Equal(PackageLoadMode.Collectible, context.LoadMode);
        Assert.Equal(["pkg-a", "pkg-b"], context.Packages.Select(static package => package.Id));
        Assert.All(context.Packages, package =>
        {
            Assert.Equal("1.0.0", package.Version);
            Assert.True(Directory.Exists(package.InstallPath));
        });
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_WhenGateBlocks_FailsGraphWithoutCreatingLoadContext()
    {
        var loader = CreateLoader(Blocking(BlockReason));
        var graph = CreateGraph("pkg-a", "pkg-b");

        var result = await loader.EnsureGraphLoadedAsync([graph], [], CancellationToken.None);

        Assert.Empty(result.Loaded);
        Assert.Equal(["pkg-a", "pkg-b"], result.FailedByPackageId.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(result.FailedByPackageId.Values, reason => Assert.Contains(BlockReason, reason, StringComparison.Ordinal));
        Assert.All(graph, package =>
        {
            Assert.False(loader.TryGetContext(package.Id, package.Version, out _));
            Assert.False(loader.IsInertPackage(package.Id, package.Version));

            var session = loader.Sessions[$"{package.Id}@{package.Version}"];
            Assert.False(session.IsLoaded);
            Assert.Contains(BlockReason, session.LastError ?? string.Empty, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_WhenGateBlocksOneGraph_LoadsTheOtherGraphs()
    {
        var blockedGraph = CreateGraph("pkg-blocked");
        var allowedGraph = CreateGraph("pkg-allowed");
        var loader = CreateLoader(new FakeActivationGate((context, _) =>
            context.Packages.Any(static package => package.Id == "pkg-blocked")
                ? PackageActivationGateResult.Block(BlockReason)
                : PackageActivationGateResult.Allow));

        var result = await loader.EnsureGraphLoadedAsync([blockedGraph, allowedGraph], [], CancellationToken.None);

        var loadedSession = Assert.Single(result.Loaded);
        Assert.Equal("pkg-allowed", loadedSession.PackageId);
        Assert.Equal("pkg-blocked", Assert.Single(result.FailedByPackageId).Key);
        Assert.True(loader.TryGetContext("pkg-allowed", "1.0.0", out _));
        Assert.False(loader.TryGetContext("pkg-blocked", "1.0.0", out _));
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_WhenMultipleGatesBlock_ReportsEveryBlockingGate()
    {
        var firstBlockingGate = Blocking("first blocker");
        var allowingGate = Allowing();
        var secondBlockingGate = Blocking("second blocker");
        var loader = CreateLoader(firstBlockingGate, allowingGate, secondBlockingGate);

        var result = await loader.EnsureGraphLoadedAsync([CreateGraph("pkg-a", "pkg-b")], [], CancellationToken.None);

        Assert.Empty(result.Loaded);
        var failure = result.FailedByPackageId["pkg-a"];
        Assert.Contains("first blocker", failure, StringComparison.Ordinal);
        Assert.Contains("second blocker", failure, StringComparison.Ordinal);
        Assert.All<FakeActivationGate>(
            [firstBlockingGate, allowingGate, secondBlockingGate],
            gate => Assert.Single(gate.Invocations));
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_WhenGateThrows_FailsClosedAndNamesTheGate()
    {
        var throwingGate = Throwing(new InvalidOperationException("schema probe exploded"));
        var laterGate = Allowing();
        var loader = CreateLoader(throwingGate, laterGate);

        var result = await loader.EnsureGraphLoadedAsync([CreateGraph("pkg-a")], [], CancellationToken.None);

        Assert.Empty(result.Loaded);
        var failure = Assert.Single(result.FailedByPackageId).Value;
        Assert.Contains(nameof(FakeActivationGate), failure, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), failure, StringComparison.Ordinal);
        Assert.Contains("schema probe exploded", failure, StringComparison.Ordinal);
        Assert.False(loader.TryGetContext("pkg-a", "1.0.0", out _));
        Assert.Single(laterGate.Invocations);
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_WhenGateHonorsCallerCancellation_PropagatesCancellationWithoutRecordingFailure()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var loader = CreateLoader(new FakeActivationGate((_, token) =>
        {
            cancellationTokenSource.Cancel();
            token.ThrowIfCancellationRequested();
            return PackageActivationGateResult.Allow;
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            loader.EnsureGraphLoadedAsync([CreateGraph("pkg-a")], [], cancellationTokenSource.Token));

        Assert.Empty(loader.Sessions);
        Assert.False(loader.TryGetContext("pkg-a", "1.0.0", out _));
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_WhenGateThrowsCancellationWithoutCallerCancellation_FailsClosed()
    {
        var loader = CreateLoader(Throwing(new OperationCanceledException("gate-owned timeout")));

        var result = await loader.EnsureGraphLoadedAsync([CreateGraph("pkg-a")], [], CancellationToken.None);

        Assert.Empty(result.Loaded);
        var failure = Assert.Single(result.FailedByPackageId).Value;
        Assert.Contains(nameof(OperationCanceledException), failure, StringComparison.Ordinal);
        Assert.False(loader.TryGetContext("pkg-a", "1.0.0", out _));
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_WhenGateReturnsNoResult_FailsClosed()
    {
        var loader = CreateLoader(new FakeActivationGate(static (_, _) => null));

        var result = await loader.EnsureGraphLoadedAsync([CreateGraph("pkg-a")], [], CancellationToken.None);

        Assert.Empty(result.Loaded);
        var failure = Assert.Single(result.FailedByPackageId).Value;
        Assert.Contains(nameof(FakeActivationGate), failure, StringComparison.Ordinal);
        Assert.Contains("no activation result", failure, StringComparison.Ordinal);
        Assert.False(loader.TryGetContext("pkg-a", "1.0.0", out _));
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_WhenBlockedGraphIsRetriedAfterGateAllows_LoadsGraph()
    {
        var blocked = true;
        var gate = new FakeActivationGate((_, _) => blocked ? PackageActivationGateResult.Block(BlockReason) : PackageActivationGateResult.Allow);
        var loader = CreateLoader(gate);
        var graph = CreateGraph("pkg-a", "pkg-b");

        var blockedResult = await loader.EnsureGraphLoadedAsync([graph], [], CancellationToken.None);
        blocked = false;
        var retriedResult = await loader.EnsureGraphLoadedAsync([graph], [], CancellationToken.None);

        Assert.Empty(blockedResult.Loaded);
        Assert.Equal(2, retriedResult.Loaded.Count);
        Assert.Empty(retriedResult.FailedByPackageId);
        Assert.Equal(2, gate.Invocations.Count);
        Assert.True(loader.TryGetContext("pkg-a", "1.0.0", out _));
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_WhenGraphIsAlreadyLoaded_DoesNotConsultGatesAgain()
    {
        var blocked = false;
        var gate = new FakeActivationGate((_, _) => blocked ? PackageActivationGateResult.Block(BlockReason) : PackageActivationGateResult.Allow);
        var loader = CreateLoader(gate);
        var graph = CreateGraph("pkg-a", "pkg-b");

        await loader.EnsureGraphLoadedAsync([graph], [], CancellationToken.None);
        blocked = true;
        var result = await loader.EnsureGraphLoadedAsync([graph], [], CancellationToken.None);

        Assert.Empty(result.FailedByPackageId);
        Assert.Equal(2, result.Loaded.Count);
        Assert.Single(gate.Invocations);
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_WhenSinglePackageGraphIsAlreadyLoaded_DoesNotConsultGatesAgain()
    {
        var blocked = false;
        var gate = new FakeActivationGate((_, _) => blocked ? PackageActivationGateResult.Block(BlockReason) : PackageActivationGateResult.Allow);
        var loader = CreateLoader(gate);
        var graph = CreateGraph("pkg-a");

        await loader.EnsureGraphLoadedAsync([graph], [], CancellationToken.None);
        blocked = true;
        var result = await loader.EnsureGraphLoadedAsync([graph], [], CancellationToken.None);

        Assert.Empty(result.FailedByPackageId);
        Assert.Single(result.Loaded);
        Assert.Single(gate.Invocations);
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_WhenGateBlocksHostIntegratedGraph_DoesNotPublishHostIntegratedVisibility()
    {
        _loadingOptions.DefaultLoadMode = PackageLoadMode.HostIntegrated;
        var gate = Blocking(BlockReason);
        var loader = CreateLoader(gate);

        var result = await loader.EnsureGraphLoadedAsync(
            [[CreatePackage("Nuplane.Loading.Tests.Fixtures")]],
            [],
            CancellationToken.None);

        Assert.Empty(result.Loaded);
        Assert.Contains(BlockReason, Assert.Single(result.FailedByPackageId).Value, StringComparison.Ordinal);
        Assert.Equal(PackageLoadMode.HostIntegrated, Assert.Single(gate.Invocations).LoadMode);
        Assert.False(loader.TryGetContext("Nuplane.Loading.Tests.Fixtures", "1.0.0", out _));
        Assert.False(_resolutionCatalog.TryResolve(typeof(FixtureMarker).Assembly.GetName(), out _, out var diagnostic));
        Assert.Equal("not-found", diagnostic.Outcome);
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_WhenHostIntegratedGraphIsAllowed_LoadsGraph()
    {
        _loadingOptions.DefaultLoadMode = PackageLoadMode.HostIntegrated;
        var loader = CreateLoader(Allowing());

        var result = await loader.EnsureGraphLoadedAsync(
            [[CreatePackage("Nuplane.Loading.Tests.Fixtures")]],
            [],
            CancellationToken.None);

        Assert.Empty(result.FailedByPackageId);
        Assert.Equal(PackageLoadMode.HostIntegrated, Assert.Single(result.Loaded).LoadMode);
        Assert.True(_resolutionCatalog.TryResolve(typeof(FixtureMarker).Assembly.GetName(), out _, out _));
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_WithEmptyGraph_DoesNotConsultGates()
    {
        var gate = Blocking(BlockReason);
        var loader = CreateLoader(gate);

        var result = await loader.EnsureGraphLoadedAsync([[]], [], CancellationToken.None);

        Assert.Empty(result.Loaded);
        Assert.Empty(result.FailedByPackageId);
        Assert.Empty(gate.Invocations);
    }

    private PackageLoader CreateLoader(params IPackageActivationGate[] gates) =>
        new(
            hostIntegratedResolutionCatalog: _resolutionCatalog,
            options: Options.Create(_loadingOptions),
            activationGates: gates);

    private IReadOnlyList<ResolvedPackage> CreateGraph(params string[] packageIds) =>
        packageIds.Select(CreatePackage).ToArray();

    private ResolvedPackage CreatePackage(string packageId)
    {
        var installDirectory = _tempDir.CreateSubdirectory(packageId);
        File.Copy(
            typeof(FixtureMarker).Assembly.Location,
            Path.Combine(installDirectory.FullName, $"{packageId}.dll"));

        return new(packageId, "1.0.0", "feed-a", installDirectory.FullName, DateTimeOffset.UtcNow, packageId);
    }

    private static FakeActivationGate Allowing() => new(static (_, _) => PackageActivationGateResult.Allow);

    private static FakeActivationGate Blocking(string reason) => new((_, _) => PackageActivationGateResult.Block(reason));

    private static FakeActivationGate Throwing(Exception exception) => new((_, _) => throw exception);

    private sealed class FakeActivationGate(
        Func<PackageActivationContext, CancellationToken, PackageActivationGateResult?> decide)
        : IPackageActivationGate
    {
        private readonly List<PackageActivationContext> _invocations = [];

        public IReadOnlyList<PackageActivationContext> Invocations => _invocations;

        public ValueTask<PackageActivationGateResult> EvaluateAsync(
            PackageActivationContext context,
            CancellationToken cancellationToken)
        {
            _invocations.Add(context);

            // The null case deliberately models a gate that answers nothing; the loader must fail closed.
            return new(decide(context, cancellationToken)!);
        }
    }
}
