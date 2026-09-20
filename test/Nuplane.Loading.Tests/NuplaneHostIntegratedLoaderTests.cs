using System.Reflection;
using Nuplane.Abstractions;

namespace Nuplane.Loading.Tests;

/// <summary>
/// Covers the host-free entry point against the property it exists for: a caller with no running host
/// loads an already-resolved active package set and gets exactly the assembly resolution a host's
/// host-integrated load gives it, through the same <c>Default.Resolving</c> hook.
/// <para>
/// Every test owns its package and assembly identities, because a host-integrated load is
/// process-global and cannot be undone.
/// </para>
/// </summary>
public sealed class NuplaneHostIntegratedLoaderTests : IDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("nuplane-host-free-");

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
    public async Task LoadActivePackagesAsync_PackageGraph_MakesAssembliesResolvableFromTheDefaultContext()
    {
        // Arrange: a root package and the dependency it binds against, activated as one graph generation.
        const string rootTypeName = "Plugin.Root.RootMarker, Plugin.Root";
        var root = HostFreeLoadTestSupport.ActivePackage(
            "Plugin.Root",
            HostFreeLoadTestSupport.CreateFixtureInstallDirectory(_tempDir, "Nuplane.Loading.Tests.Fixtures.Root", "Plugin.Root.dll"),
            graphGenerationId: "generation-plugin");
        var dependency = HostFreeLoadTestSupport.ActivePackage(
            "Plugin.Dependency",
            HostFreeLoadTestSupport.CreateFixtureInstallDirectory(_tempDir, "Nuplane.Loading.Tests.Fixtures.Dependency", "Plugin.Dependency.dll"),
            graphGenerationId: "generation-plugin");

        // The fixture assemblies are deliberately not referenced by this test project, so nothing can
        // see them before the load. Without this the assertions below would prove nothing.
        Assert.Null(Type.GetType(rootTypeName));

        // Act
        var result = await NuplaneHostIntegratedLoader.LoadActivePackagesAsync([root, dependency]);

        // Assert: both packages loaded host-integrated, into one graph context.
        Assert.Empty(result.FailedByPackageId);
        Assert.All(result.Packages, package =>
        {
            Assert.Equal(PackageLoadStatus.Loaded, package.Status);
            Assert.Equal(PackageLoadMode.HostIntegrated, package.LoadMode);
            Assert.True(package.FrameworkIntegrationSafe);
        });
        Assert.Equal(1, HostFreeLoadTestSupport.CountGraphLoadContexts(root, dependency));

        // ... and the three by-name resolution paths an out-of-process tool actually uses now see them.
        var rootType = Type.GetType(rootTypeName);
        Assert.NotNull(rootType);
        Assert.NotNull(Assembly.Load(new AssemblyName("Plugin.Dependency")));
        var loadedAssemblyNames = AppDomain.CurrentDomain.GetAssemblies()
            .Select(static assembly => assembly.GetName().Name)
            .ToArray();
        Assert.Contains("Plugin.Root", loadedAssemblyNames);
        Assert.Contains("Plugin.Dependency", loadedAssemblyNames);

        // The dependency edge binds inside the graph context, exactly as it does in a host.
        Assert.Equal(
            "root:dependency",
            rootType.GetProperty("Value")!.GetValue(Activator.CreateInstance(rootType)));
    }

    [Fact]
    public async Task LoadActivePackagesAsync_CalledTwiceForTheSameSet_InstallsOneHookAndLoadsTheGraphOnce()
    {
        // Arrange
        var package = HostFreeLoadTestSupport.EmitPackage(_tempDir, "Nuplane.HostFree.Repeat").AsActivePackage();

        // Act
        var first = await NuplaneHostIntegratedLoader.LoadActivePackagesAsync([package]);
        var firstAssembly = Assembly.Load(new AssemblyName(package.PackageId));
        var second = await NuplaneHostIntegratedLoader.LoadActivePackagesAsync([package]);

        // Assert: the second call reports the first call's load rather than repeating it. Host-integrated
        // contexts are non-collectible, so a second load would leave two copies of the same assembly in
        // the process and by-name resolution could then answer with either.
        Assert.Equal(PackageLoadStatus.Loaded, Assert.Single(first.Packages).Status);
        Assert.Equal(PackageLoadStatus.Loaded, Assert.Single(second.Packages).Status);
        Assert.Equal(Assert.Single(first.Packages).AssemblyReferences, Assert.Single(second.Packages).AssemblyReferences);
        Assert.Equal(1, HostFreeLoadTestSupport.CountGraphLoadContexts(package));
        Assert.Same(firstAssembly, Assembly.Load(new AssemblyName(package.PackageId)));

        // The Default.Resolving hook is installed exactly once per process however often this is called.
        Assert.Equal(1, HostIntegratedLoadComposition.ResolverInstallCount);
    }

    [Fact]
    public async Task LoadActivePackagesAsync_WhenActivationGateBlocks_ReportsOrdinaryFailureAndLoadsNothing()
    {
        // Arrange
        const string blockReason = "the worker's schema version is behind this package.";
        var emitted = HostFreeLoadTestSupport.EmitPackage(_tempDir, "Nuplane.HostFree.Blocked");
        var package = emitted.AsActivePackage();
        var options = new HostIntegratedLoadOptions();
        options.ActivationGates.Add(new StubActivationGate(blockReason));

        // Act
        var result = await NuplaneHostIntegratedLoader.LoadActivePackagesAsync([package], options);

        // Assert: an ordinary load failure, not an exception, and nothing of the package in the process.
        var state = Assert.Single(result.Packages);
        Assert.Equal(PackageLoadStatus.Failed, state.Status);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Contains(blockReason, StringComparison.Ordinal));
        Assert.Contains(blockReason, Assert.Single(result.FailedByPackageId).Value, StringComparison.Ordinal);
        Assert.Null(Type.GetType(emitted.MarkerTypeName));
        Assert.Equal(0, HostFreeLoadTestSupport.CountGraphLoadContexts(package));
    }

    [Fact]
    public async Task LoadActivePackagesAsync_AfterAnActivationGateBlocked_LoadsOnTheNextCallWithoutTheGate()
    {
        // Arrange: the same refusal as above, so the test pins the direction that must NOT be sticky —
        // a blocked graph must not be remembered as loaded, or the caller could never retry it.
        var emitted = HostFreeLoadTestSupport.EmitPackage(_tempDir, "Nuplane.HostFree.Retried");
        var package = emitted.AsActivePackage();
        var blockingOptions = new HostIntegratedLoadOptions();
        blockingOptions.ActivationGates.Add(new StubActivationGate("not yet."));
        await NuplaneHostIntegratedLoader.LoadActivePackagesAsync([package], blockingOptions);

        // Act: the caller fixes the pre-condition and loads again, with no gate this time.
        var result = await NuplaneHostIntegratedLoader.LoadActivePackagesAsync([package]);

        // Assert
        Assert.Empty(result.FailedByPackageId);
        Assert.Equal(PackageLoadStatus.Loaded, Assert.Single(result.Packages).Status);
        Assert.NotNull(Type.GetType(emitted.MarkerTypeName));
        Assert.Equal(1, HostFreeLoadTestSupport.CountGraphLoadContexts(package));
    }

    [Fact]
    public async Task LoadActivePackagesAsync_WithTargetFrameworkOverride_SelectsAssetsForTheOverriddenFramework()
    {
        // Arrange: one package with an asset per framework, each stamped with its own assembly version.
        var emitted = HostFreeLoadTestSupport.EmitMultiTargetPackage(
            _tempDir,
            "Nuplane.HostFree.Overridden",
            ("net8.0", new Version(8, 0, 0, 0)),
            ("net10.0", new Version(10, 0, 0, 0)));
        var package = emitted.AsActivePackage();

        // Act: ask for the assets a net8.0 host would have selected, from a net10.0 test process.
        var result = await NuplaneHostIntegratedLoader.LoadActivePackagesAsync(
            [package],
            new HostIntegratedLoadOptions { TargetFrameworkOverride = "net8.0" });

        // Assert: the override reached asset selection, not just the option bag.
        var state = Assert.Single(result.Packages);
        Assert.Equal(PackageLoadStatus.Loaded, state.Status);
        var reference = Assert.Single(state.AssemblyReferences);
        Assert.Equal("PrimaryLoadAssembly", reference.Kind);
        Assert.Equal("net8.0", reference.TargetFrameworkMoniker);
        Assert.Equal(8, Assembly.Load(new AssemblyName(emitted.AssemblyName)).GetName().Version!.Major);
    }

    [Fact]
    public async Task LoadActivePackagesAsync_WithoutTargetFrameworkOverride_SelectsAssetsForTheCurrentProcess()
    {
        // Arrange: the same shape as the override test, so the two together show the override is what
        // changed the outcome and that one call's options never leak into another call.
        var emitted = HostFreeLoadTestSupport.EmitMultiTargetPackage(
            _tempDir,
            "Nuplane.HostFree.Current",
            ("net8.0", new Version(8, 0, 0, 0)),
            ("net10.0", new Version(10, 0, 0, 0)));
        var package = emitted.AsActivePackage();

        // Act
        var result = await NuplaneHostIntegratedLoader.LoadActivePackagesAsync([package]);

        // Assert
        var state = Assert.Single(result.Packages);
        Assert.Equal(PackageLoadStatus.Loaded, state.Status);
        Assert.Equal("net10.0", Assert.Single(state.AssemblyReferences).TargetFrameworkMoniker);
        Assert.Equal(10, Assembly.Load(new AssemblyName(emitted.AssemblyName)).GetName().Version!.Major);
    }

    [Fact]
    public async Task LoadActivePackagesAsync_WhenTheSameGraphIsAskedForADifferentTargetFramework_Throws()
    {
        // Arrange: the graph is already in the process, loaded for the current process's framework.
        var emitted = HostFreeLoadTestSupport.EmitMultiTargetPackage(
            _tempDir,
            "Nuplane.HostFree.Reframed",
            ("net8.0", new Version(8, 0, 0, 0)),
            ("net10.0", new Version(10, 0, 0, 0)));
        var package = emitted.AsActivePackage();
        await NuplaneHostIntegratedLoader.LoadActivePackagesAsync([package]);

        // Act: a second call asks for the assets a net8.0 host would have selected. The load cannot be
        // undone, so those assets can never be the ones in the process; answering with the loaded
        // net10.0 assets would silently answer a question the caller did not ask.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NuplaneHostIntegratedLoader.LoadActivePackagesAsync(
                [package],
                new HostIntegratedLoadOptions { TargetFrameworkOverride = "net8.0" }));

        // Assert: nothing changed in the process because of the rejected call.
        Assert.Contains("net8.0", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, HostFreeLoadTestSupport.CountGraphLoadContexts(package));
        Assert.Equal(10, Assembly.Load(new AssemblyName(emitted.AssemblyName)).GetName().Version!.Major);
    }

    [Fact]
    public async Task LoadActivePackagesAsync_WhenInstallPathIsMissingOnDisk_ReportsOrdinaryLoadFailure()
    {
        // Arrange
        var missingInstallPath = Path.Combine(_tempDir.FullName, $"missing-{Guid.NewGuid():N}");
        var package = HostFreeLoadTestSupport.ActivePackage("Nuplane.HostFree.Missing", missingInstallPath);

        // Act
        var result = await NuplaneHostIntegratedLoader.LoadActivePackagesAsync([package]);

        // Assert
        var state = Assert.Single(result.Packages);
        Assert.Equal(PackageLoadStatus.Failed, state.Status);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Contains(missingInstallPath, StringComparison.Ordinal));
        Assert.Contains("Nuplane.HostFree.Missing", result.FailedByPackageId.Keys);
    }

    [Fact]
    public async Task LoadActivePackagesAsync_WhenPackageSetIsEmpty_ReturnsEmptyResult()
    {
        var result = await NuplaneHostIntegratedLoader.LoadActivePackagesAsync([]);

        Assert.Empty(result.Packages);
        Assert.Empty(result.FailedByPackageId);
    }

    [Fact]
    public async Task LoadActivePackagesAsync_WhenPackagesIsNull_Throws() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() => NuplaneHostIntegratedLoader.LoadActivePackagesAsync(null!));

    [Fact]
    public async Task LoadActivePackagesAsync_WhenPackageIdentityIsIncomplete_Throws()
    {
        var package = HostFreeLoadTestSupport.ActivePackage("Nuplane.HostFree.Blank", "   ");

        await Assert.ThrowsAsync<ArgumentException>(() => NuplaneHostIntegratedLoader.LoadActivePackagesAsync([package]));
    }

    [Fact]
    public async Task LoadActivePackagesAsync_WhenTheSamePackageIdAppearsTwice_Throws()
    {
        var package = HostFreeLoadTestSupport.ActivePackage("Nuplane.HostFree.Duplicated", _tempDir.FullName);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            NuplaneHostIntegratedLoader.LoadActivePackagesAsync([package, package with { Version = "2.0.0" }]));

        Assert.Contains("Nuplane.HostFree.Duplicated", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadActivePackagesAsync_WhenTargetFrameworkOverrideIsNotAMoniker_Throws()
    {
        var package = HostFreeLoadTestSupport.ActivePackage("Nuplane.HostFree.BadFramework", _tempDir.FullName);

        // A framework Nuplane cannot parse must not be silently ignored: a package with no framework
        // folders would then load as if the caller had never asked for a different framework.
        await Assert.ThrowsAsync<ArgumentException>(() => NuplaneHostIntegratedLoader.LoadActivePackagesAsync(
            [package],
            new HostIntegratedLoadOptions { TargetFrameworkOverride = "netcoreapp-banana" }));
    }

    [Fact]
    public async Task LoadActivePackagesAsync_WhenAComposedHostAlreadyLoadedTheGraph_RefusesInsteadOfLoadingItTwice()
    {
        // Arrange: a real DI-composed host loads the package set host-integrated first.
        var emitted = HostFreeLoadTestSupport.EmitPackage(_tempDir, "Nuplane.HostFree.Shared");
        var package = emitted.AsActivePackage();
        await using var host = new NuplaneHostFixture();
        await host.ActivateAsync(package);
        Assert.Equal(PackageLoadStatus.Loaded, Assert.Single((await host.ReadLoadStateAsync()).Packages).Status);

        // Act
        var result = await NuplaneHostIntegratedLoader.LoadActivePackagesAsync([package]);

        // Assert: refused as an ordinary load failure rather than loaded into a second non-collectible
        // context, which would leave the process with two copies of the same assemblies.
        var state = Assert.Single(result.Packages);
        Assert.Equal(PackageLoadStatus.Failed, state.Status);
        Assert.Contains(
            state.Diagnostics,
            diagnostic => diagnostic.Contains("another Nuplane composition in this process", StringComparison.Ordinal));
        Assert.Equal(1, HostFreeLoadTestSupport.CountGraphLoadContexts(package));
        Assert.Equal(1, HostIntegratedLoadComposition.ResolverInstallCount);
    }

    private sealed class StubActivationGate(string blockReason) : IPackageActivationGate
    {
        public ValueTask<PackageActivationGateResult> EvaluateAsync(
            PackageActivationContext context,
            CancellationToken cancellationToken) =>
            new(PackageActivationGateResult.Block(blockReason));
    }
}
