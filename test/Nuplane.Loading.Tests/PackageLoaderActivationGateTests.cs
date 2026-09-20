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
    private const string DefaultVersion = "1.0.0";
    private const string UpdatePackageId = "pkg-updated";
    private const string UpdateVersion = "2.0.0";

    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("nuplane-activation-gate-test-");
    private readonly HostIntegratedAssemblyResolutionCatalog _resolutionCatalog = new();
    private readonly LoadingOptions _loadingOptions = new();
    private int _installDirectoryCount;

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

    [Theory]
    [InlineData(PackageLoadMode.Collectible)]
    [InlineData(PackageLoadMode.HostIntegrated)]
    public async Task EnsureGraphLoadedAsync_WhenUpdateGraphIsBlocked_LeavesLoadedVersionExactlyAsAResolutionFailureWould(
        PackageLoadMode loadMode)
    {
        var blockedUpdate = await RunUpdateAttemptAsync(loadMode, blockUpdateWithGate: true);
        var genuinelyFailedUpdate = await RunUpdateAttemptAsync(loadMode, blockUpdateWithGate: false);

        Assert.All<UpdateAttempt>([blockedUpdate, genuinelyFailedUpdate], attempt =>
        {
            // The version that is already loaded keeps its live context, session and host-integrated visibility.
            Assert.True(attempt.Loader.TryGetContext(UpdatePackageId, DefaultVersion, out var loadedContext));
            Assert.NotNull(loadedContext!.Context);
            var loadedSession = attempt.Loader.Sessions[$"{UpdatePackageId}@{DefaultVersion}"];
            Assert.True(loadedSession.IsLoaded);
            Assert.Null(loadedSession.LastError);
            Assert.Equal(loadMode, loadedSession.LoadMode);
            Assert.Equal(
                loadMode == PackageLoadMode.HostIntegrated,
                attempt.ResolutionCatalog.TryResolve(typeof(FixtureMarker).Assembly.GetName(), out _, out _));

            // The update version is failed under its own version-scoped key and never got a context. The
            // id-keyed failure map reports the package id while the loaded version keeps running.
            Assert.Empty(attempt.Result.Loaded);
            Assert.False(attempt.Loader.TryGetContext(UpdatePackageId, UpdateVersion, out _));
            var updateSession = attempt.Loader.Sessions[$"{UpdatePackageId}@{UpdateVersion}"];
            Assert.False(updateSession.IsLoaded);
            Assert.False(string.IsNullOrWhiteSpace(updateSession.LastError));
            Assert.Equal(UpdatePackageId, Assert.Single(attempt.Result.FailedByPackageId).Key);
        });

        Assert.Contains(BlockReason, blockedUpdate.Result.FailedByPackageId[UpdatePackageId], StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_WhenBlockedGraphHoldsHostRuntimePackage_FailsItAndRestoresInertStateOnceAllowed()
    {
        var blocked = true;
        var loader = CreateLoader(new FakeActivationGate((_, _) =>
            blocked ? PackageActivationGateResult.Block(BlockReason) : PackageActivationGateResult.Allow));
        var hostRuntimePackage = CreateHostRuntimePackage("System.Memory");
        var packageKey = $"{hostRuntimePackage.Id}@{hostRuntimePackage.Version}";

        var blockedResult = await loader.EnsureGraphLoadedAsync([[hostRuntimePackage]], [], CancellationToken.None);

        // The graph is refused before it is resolved, so even the member a successful load would have skipped
        // as host-provided is reported failed with the gate's reason.
        Assert.Contains(BlockReason, Assert.Single(blockedResult.FailedByPackageId).Value, StringComparison.Ordinal);
        Assert.False(loader.Sessions[packageKey].IsLoaded);
        Assert.False(loader.IsInertPackage(hostRuntimePackage.Id, hostRuntimePackage.Version));

        blocked = false;
        var allowedResult = await loader.EnsureGraphLoadedAsync([[hostRuntimePackage]], [], CancellationToken.None);

        // Once allowed, the package returns to exactly the state a first, never-blocked attempt leaves:
        // inert, with no session and no lingering failure.
        Assert.Empty(allowedResult.FailedByPackageId);
        Assert.Empty(allowedResult.Loaded);
        Assert.True(loader.IsInertPackage(hostRuntimePackage.Id, hostRuntimePackage.Version));
        Assert.Empty(loader.Sessions);
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_WhenBlockedGraphHoldsSkippedPackage_FailsEveryMemberAndRestoresSkippedStateOnceAllowed()
    {
        var blocked = true;
        var loader = CreateLoader(new FakeActivationGate((_, _) =>
            blocked ? PackageActivationGateResult.Block(BlockReason) : PackageActivationGateResult.Allow));
        var root = CreatePackage("pkg-root");
        var facade = CreateFacadePackage("pkg-facade");
        var graph = new[] { root, facade };

        var blockedResult = await loader.EnsureGraphLoadedAsync([graph], [], CancellationToken.None);

        // Every member of the refused graph is failed, including the facade a successful load would skip.
        Assert.Equal(["pkg-facade", "pkg-root"], blockedResult.FailedByPackageId.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(graph, package =>
            Assert.Contains(BlockReason, blockedResult.FailedByPackageId[package.Id], StringComparison.Ordinal));
        Assert.False(loader.IsInertPackage(facade.Id, facade.Version));

        blocked = false;
        var allowedResult = await loader.EnsureGraphLoadedAsync([graph], [], CancellationToken.None);

        // Once allowed, the facade is inert again and leaves no failed session behind.
        Assert.Empty(allowedResult.FailedByPackageId);
        Assert.Equal("pkg-root", Assert.Single(allowedResult.Loaded).PackageId);
        Assert.True(loader.IsInertPackage(facade.Id, facade.Version));
        Assert.Equal($"{root.Id}@{root.Version}", Assert.Single(loader.Sessions).Key);
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

    /// <summary>
    /// Loads <c>pkg-updated</c> 1.0.0, then attempts to load 2.0.0 of the same package in a way that cannot
    /// succeed: either an activation gate refuses it, or it genuinely fails to resolve because it is not
    /// installed. Both attempts must leave the loaded 1.0.0 identical.
    /// </summary>
    private async Task<UpdateAttempt> RunUpdateAttemptAsync(PackageLoadMode loadMode, bool blockUpdateWithGate)
    {
        _loadingOptions.DefaultLoadMode = loadMode;
        var resolutionCatalog = new HostIntegratedAssemblyResolutionCatalog();
        var loader = CreateLoader(
            resolutionCatalog,
            new FakeActivationGate((context, _) =>
                blockUpdateWithGate && context.Packages.Any(static package => package.Version == UpdateVersion)
                    ? PackageActivationGateResult.Block(BlockReason)
                    : PackageActivationGateResult.Allow));

        var loadedResult = await loader.EnsureGraphLoadedAsync(
            [[CreatePackage(UpdatePackageId, DefaultVersion)]],
            [],
            CancellationToken.None);
        Assert.Single(loadedResult.Loaded);

        var update = blockUpdateWithGate
            ? CreatePackage(UpdatePackageId, UpdateVersion)
            : CreateResolvedPackage(UpdatePackageId, UpdateVersion, Path.Combine(_tempDir.FullName, "never-installed"));

        return new(
            loader,
            resolutionCatalog,
            await loader.EnsureGraphLoadedAsync([[update]], [], CancellationToken.None));
    }

    private PackageLoader CreateLoader(params IPackageActivationGate[] gates) =>
        CreateLoader(_resolutionCatalog, gates);

    private PackageLoader CreateLoader(
        HostIntegratedAssemblyResolutionCatalog resolutionCatalog,
        params IPackageActivationGate[] gates) =>
        new(
            hostIntegratedResolutionCatalog: resolutionCatalog,
            options: Options.Create(_loadingOptions),
            activationGates: gates);

    private IReadOnlyList<ResolvedPackage> CreateGraph(params string[] packageIds) =>
        packageIds.Select(packageId => CreatePackage(packageId)).ToArray();

    private ResolvedPackage CreatePackage(string packageId, string version = DefaultVersion)
    {
        var installDirectory = CreateInstallDirectory(packageId, version);
        File.Copy(
            typeof(FixtureMarker).Assembly.Location,
            Path.Combine(installDirectory, $"{packageId}.dll"));

        return CreateResolvedPackage(packageId, version, installDirectory);
    }

    /// <summary>
    /// Creates a package whose only assembly is provided by the host runtime, which the loader evaluates as an
    /// inert graph member: it is neither loaded nor failed by a successful load attempt.
    /// </summary>
    private ResolvedPackage CreateHostRuntimePackage(string assemblyName)
    {
        var trustedPlatformAssemblies = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var hostAssemblyPath = trustedPlatformAssemblies.FirstOrDefault(path =>
            string.Equals(Path.GetFileNameWithoutExtension(path), assemblyName, StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(hostAssemblyPath));

        var libDirectory = Directory.CreateDirectory(
            Path.Combine(CreateInstallDirectory(assemblyName, DefaultVersion), "lib", "net10.0"));
        File.Copy(hostAssemblyPath, Path.Combine(libDirectory.FullName, $"{assemblyName}.dll"));

        return CreateResolvedPackage(assemblyName, DefaultVersion, libDirectory.Parent!.Parent!.FullName);
    }

    /// <summary>
    /// Creates a facade package that carries no assembly, which the loader skips as an inert graph member
    /// rather than failing when the graph around it loads.
    /// </summary>
    private ResolvedPackage CreateFacadePackage(string packageId)
    {
        var installDirectory = CreateInstallDirectory(packageId, DefaultVersion);
        var libDirectory = Directory.CreateDirectory(Path.Combine(installDirectory, "lib", "netstandard2.0"));
        File.WriteAllText(Path.Combine(libDirectory.FullName, "_._"), string.Empty);

        return CreateResolvedPackage(packageId, DefaultVersion, installDirectory);
    }

    // Each call gets its own directory so the same package identity can be installed more than once in a test
    // without one scenario overwriting an assembly another scenario already loaded.
    private string CreateInstallDirectory(string packageId, string version) =>
        _tempDir.CreateSubdirectory($"{packageId}-{version}-{_installDirectoryCount++}").FullName;

    private static ResolvedPackage CreateResolvedPackage(string packageId, string version, string installPath) =>
        new(packageId, version, "feed-a", installPath, DateTimeOffset.UtcNow, packageId);

    private static FakeActivationGate Allowing() => new(static (_, _) => PackageActivationGateResult.Allow);

    private static FakeActivationGate Blocking(string reason) => new((_, _) => PackageActivationGateResult.Block(reason));

    private static FakeActivationGate Throwing(Exception exception) => new((_, _) => throw exception);

    private sealed record UpdateAttempt(
        PackageLoader Loader,
        HostIntegratedAssemblyResolutionCatalog ResolutionCatalog,
        PackageLoadResult Result);

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
