using System.Reflection;
using Nuplane.Store.State;

namespace Nuplane.Loading.Tests;

/// <summary>
/// The acceptance property of the host-free entry point: for the same package set it must behave like a
/// real host's host-integrated load, not merely load the same files. These tests load one package set
/// through both paths in one process and compare what each reports, and drive the two-step flow the
/// entry point exists for — read the active set from a persisted store, then load it without a host.
/// </summary>
public sealed class NuplaneHostIntegratedLoaderParityTests : IDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("nuplane-host-free-parity-");

    public void Dispose()
    {
        try
        {
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
    public async Task LoadActivePackagesAsync_ForTheSameSetAsAComposedHost_ReportsTheSameLoadState()
    {
        // Arrange: a two-package graph, so graph grouping, graph keys and dependency-closure load-mode
        // promotion all have to agree between the two paths, not just single-package asset selection.
        const string graphGenerationId = "generation-parity";
        var first = HostFreeLoadTestSupport.EmitPackage(_tempDir, "Nuplane.HostFree.ParityA").AsActivePackage(graphGenerationId);
        var second = HostFreeLoadTestSupport.EmitPackage(_tempDir, "Nuplane.HostFree.ParityB").AsActivePackage(graphGenerationId);

        // Act: the host-free load runs first, because a set a composed host has already loaded into a
        // non-collectible context is deliberately refused rather than loaded a second time.
        var hostFree = await NuplaneHostIntegratedLoader.LoadActivePackagesAsync([first, second]);

        await using var host = new NuplaneHostFixture();
        await host.ActivateAsync(first, second);
        var hostState = await host.ReadLoadStateAsync();

        // Assert
        Assert.Empty(hostFree.FailedByPackageId);
        Assert.Equal(PackageLoadStateAvailability.Available, hostState.Availability);
        Assert.Equal(2, hostFree.Packages.Count);
        Assert.Equal(2, hostState.Packages.Count);

        foreach (var expected in hostState.Packages)
        {
            var actual = Assert.Single(hostFree.Packages, package => package.PackageId == expected.PackageId);

            Assert.Equal(expected.Version, actual.Version);
            Assert.Equal(expected.Status, actual.Status);
            Assert.Equal(expected.InstallPath, actual.InstallPath);
            Assert.Equal(expected.LoadMode, actual.LoadMode);
            Assert.Equal(expected.FrameworkIntegrationSafe, actual.FrameworkIntegrationSafe);
            Assert.Equal(expected.Discoverable, actual.Discoverable);
            Assert.Equal(expected.Diagnostics, actual.Diagnostics);

            // Selected asset paths, main-assembly choice and selection reasons.
            Assert.NotEmpty(expected.AssemblyReferences);
            Assert.Equal(expected.AssemblyReferences, actual.AssemblyReferences);

            // Load-mode decision, including the graph key both paths derived from the same package set.
            Assert.NotEmpty(expected.LoadModeDiagnostics!);
            Assert.Equal(expected.LoadModeDiagnostics, actual.LoadModeDiagnostics);
        }

        Assert.Equal(PackageLoadStatus.Loaded, hostState.Packages[0].Status);
        Assert.Equal(PackageLoadMode.HostIntegrated, hostState.Packages[0].LoadMode);
    }

    [Fact]
    public async Task LoadActivePackagesAsync_FromAPersistedStoreStateFile_LoadsWhatTheStoreRecorded()
    {
        // Arrange: a real store writes store-state.json, exactly as a running host leaves it behind.
        const string graphGenerationId = "generation-store";
        var stateFilePath = Path.Combine(_tempDir.FullName, "store-state.json");
        var emittedRoot = HostFreeLoadTestSupport.EmitPackage(_tempDir, "Nuplane.HostFree.StoreRoot");
        var emittedDependency = HostFreeLoadTestSupport.EmitPackage(_tempDir, "Nuplane.HostFree.StoreDependency");

        await using (var writingHost = new NuplaneHostFixture(stateFilePath, withLoading: false))
        {
            await writingHost.ActivateAsync(
                emittedRoot.AsActivePackage(graphGenerationId),
                emittedDependency.AsActivePackage(graphGenerationId));
        }

        // Act: the whole two-step flow in one call — the entry point performs the same strictly
        // read-only offline read NuplaneStore does, then loads what the state records.
        var result = await NuplaneHostIntegratedLoader.LoadFromStateAsync(stateFilePath);

        // Assert
        var activePackages = await NuplaneStore.ReadActivePackagesAsync(stateFilePath);
        Assert.Equal(2, activePackages.Count);
        Assert.All(activePackages, package => Assert.Equal(graphGenerationId, package.GraphGenerationId));
        Assert.Equal(2, result.Packages.Count);
        Assert.Empty(result.FailedByPackageId);
        Assert.All(result.Packages, package =>
        {
            Assert.Equal(PackageLoadStatus.Loaded, package.Status);
            Assert.Equal(PackageLoadMode.HostIntegrated, package.LoadMode);
        });

        // The activation the store recorded decided the grouping: one context for both packages.
        Assert.Equal(1, HostFreeLoadTestSupport.CountGraphLoadContexts([.. activePackages]));
        Assert.NotNull(Type.GetType(emittedRoot.MarkerTypeName));
        Assert.NotNull(Assembly.Load(new AssemblyName(emittedDependency.AssemblyName)));
    }

    [Fact]
    public async Task LoadActivePackagesAsync_FromStoreRegistryOptions_ResolvesTheSameStateFileAHostWould()
    {
        // Arrange
        var stateFilePath = Path.Combine(_tempDir.FullName, "options-store-state.json");
        var emitted = HostFreeLoadTestSupport.EmitPackage(_tempDir, "Nuplane.HostFree.StoreOptions");

        await using (var writingHost = new NuplaneHostFixture(stateFilePath, withLoading: false))
        {
            await writingHost.ActivateAsync(emitted.AsActivePackage());
        }

        // Act
        var result = await NuplaneHostIntegratedLoader.LoadFromStateAsync(
            new StoreRegistryOptions { StateFilePath = stateFilePath });

        // Assert
        Assert.Empty(result.FailedByPackageId);
        Assert.Equal(PackageLoadStatus.Loaded, Assert.Single(result.Packages).Status);
        Assert.NotNull(Type.GetType(emitted.MarkerTypeName));
    }

    [Fact]
    public async Task LoadActivePackagesAsync_FromStoreRegistryOptionsWithInMemoryPersistence_Throws()
    {
        // There is no state file to read, and answering "nothing is active" would let a caller mistake
        // wrong options for an empty host.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NuplaneHostIntegratedLoader.LoadFromStateAsync(new StoreRegistryOptions { UseInMemoryStore = true }));
    }
}
