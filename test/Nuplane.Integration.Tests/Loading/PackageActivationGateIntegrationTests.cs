using Microsoft.Extensions.DependencyInjection;
using Nuplane;
using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Loading;
using Nuplane.Loading.Hosting.Builder;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Loading;

/// <summary>
/// Proves the activation gate contract through the real composition: a gate registered with
/// <see cref="NuplaneLoadingBuilder.AddActivationGate{TGate}"/> reaches the DI-resolved
/// <see cref="PackageLoader"/> and is consulted on the only production load path —
/// the reconciled-observer callback that both startup and reconcile drive — and shows what an operator
/// then reads from the load-state surface.
/// </summary>
public sealed class PackageActivationGateIntegrationTests : IAsyncDisposable
{
    private const string PackageId = "pkg-gated";
    private const string Version = "1.0.0";
    private const string CorrelationId = "corr-activation-gate";
    private const string BlockReason = "module schema version is behind the package.";

    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("nuplane-activation-gate-integration-");
    private readonly ServiceProvider _provider;
    private readonly ResolvedPackage _package;
    private readonly ToggleActivationGate _gate;

    public PackageActivationGateIntegrationTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(nuplane =>
        {
            nuplane.UseInMemoryStore();
            nuplane.AutoloadPackages(loading => loading.AddActivationGate<ToggleActivationGate>());
        });

        _provider = services.BuildServiceProvider();
        _package = new(PackageId, Version, "feed-a", CreateInstallDirectory(), DateTimeOffset.UtcNow, "source-a");
        _gate = _provider.GetRequiredService<ToggleActivationGate>();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();

        try
        {
            _tempDir.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ReconciledObserverPath_WhenRegisteredGateBlocks_ConsultsGateAndLeavesPackageUnloaded()
    {
        await PersistActivePackageAsync();
        _gate.IsBlocked = true;

        await ReconcileAsync();

        // The gate registered through the builder was reached by the DI-resolved loader.
        var context = Assert.Single(_gate.Invocations);
        var gatedPackage = Assert.Single(context.Packages);
        Assert.Equal(PackageId, gatedPackage.Id);
        Assert.Equal(_package.InstallPath, gatedPackage.InstallPath);

        // Nothing of the package was loaded: no load context, no assemblies.
        var loader = _provider.GetRequiredService<PackageLoader>();
        Assert.False(loader.TryGetContext(PackageId, Version, out _));
        Assert.Null(await _provider.GetRequiredService<IPackageAssemblyCatalog>().GetPackagedAssembliesAsync(PackageId, CancellationToken.None));

        // The operator reads a failed package that is still active, with the gate's reason attached.
        var package = Assert.Single((await ReadLoadStateAsync()).Packages);
        Assert.Equal(PackageLoadStatus.Failed, package.Status);
        Assert.Contains(package.Diagnostics, diagnostic => diagnostic.Contains(BlockReason, StringComparison.Ordinal));

        var storeState = await _provider.GetRequiredService<IStoreRegistry>().GetStateAsync(CancellationToken.None);
        Assert.Contains(PackageId, storeState.ActiveVersionById.Keys);
        Assert.Equal("load", storeState.LastFailureById[PackageId].Stage);
        Assert.Contains(BlockReason, storeState.LastFailureById[PackageId].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReconciledObserverPath_WhenRegisteredGateAllows_LoadsPackageAsUsual()
    {
        await PersistActivePackageAsync();
        _gate.IsBlocked = false;

        await ReconcileAsync();

        Assert.Single(_gate.Invocations);
        Assert.True(_provider.GetRequiredService<PackageLoader>().TryGetContext(PackageId, Version, out _));
        Assert.Equal(PackageLoadStatus.Loaded, Assert.Single((await ReadLoadStateAsync()).Packages).Status);
    }

    [Fact]
    public async Task ReconciledObserverPath_WhenGateAllowsAfterBlocking_LoadsOnTheNextReconcile()
    {
        await PersistActivePackageAsync();
        _gate.IsBlocked = true;
        await ReconcileAsync();

        // The operator fixes the pre-condition; the next reconcile re-evaluates the gate.
        _gate.IsBlocked = false;
        await ReconcileAsync();

        Assert.Equal(2, _gate.Invocations.Count);
        Assert.True(_provider.GetRequiredService<PackageLoader>().TryGetContext(PackageId, Version, out _));
        Assert.Equal(PackageLoadStatus.Loaded, Assert.Single((await ReadLoadStateAsync()).Packages).Status);
    }

    // Drives the exact call the reconciliation pipeline and last-known-good startup recovery make.
    private Task ReconcileAsync() =>
        _provider.GetRequiredService<IObserverEventDispatcher>().PublishReconciledAsync(
            new PackageChangeSet([], [], [], CorrelationId, DateTimeOffset.UtcNow),
            [_package],
            CancellationToken.None);

    private Task<PackageLoadStateSnapshot> ReadLoadStateAsync() =>
        _provider.GetRequiredService<IPackageLoadStateCatalog>().GetLoadStateAsync(CancellationToken.None);

    private Task PersistActivePackageAsync() =>
        _provider.GetRequiredService<IStoreRegistry>().PersistActiveVersionsAsync(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [PackageId] = Version },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [PackageId] = Version },
            CorrelationId,
            CancellationToken.None,
            new Dictionary<string, ActivePackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                [PackageId] = new(
                    PackageId,
                    Version,
                    "feed-a",
                    "source-a",
                    _package.InstallPath,
                    DateTimeOffset.UtcNow,
                    CorrelationId)
            });

    private string CreateInstallDirectory()
    {
        var installDirectory = _tempDir.CreateSubdirectory(PackageId);
        var sourceAssembly = typeof(PackageLoader).Assembly.Location;
        File.Copy(sourceAssembly, Path.Combine(installDirectory.FullName, Path.GetFileName(sourceAssembly)));

        return installDirectory.FullName;
    }

    internal sealed class ToggleActivationGate : IPackageActivationGate
    {
        private readonly List<PackageActivationContext> _invocations = [];

        public bool IsBlocked { get; set; }

        public IReadOnlyList<PackageActivationContext> Invocations => _invocations;

        public ValueTask<PackageActivationGateResult> EvaluateAsync(
            PackageActivationContext context,
            CancellationToken cancellationToken)
        {
            _invocations.Add(context);

            return new(IsBlocked
                ? PackageActivationGateResult.Block(BlockReason)
                : PackageActivationGateResult.Allow);
        }
    }
}
