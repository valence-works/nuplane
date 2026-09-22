using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Hosting;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Runtime.Tests.TestSupport;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Hosting;

/// <summary>
/// The invariant these pin: last-known-good startup recovery takes the same cross-process store
/// lock a reconciliation cycle takes around its read-then-republish of the state file, so a
/// recovery racing a concurrent writer on the same store never acts on it — mirroring
/// <c>ReconciliationServiceStoreLockTests</c> for <see cref="LastKnownGoodStartupRecoveryService"/>.
/// </summary>
public sealed class LastKnownGoodStartupRecoveryServiceStoreLockTests : IDisposable
{
    private const string CorrelationId = "corr-recovery";

    private readonly TempDirectory _root = new();
    private readonly string _stateFilePath;
    private readonly string _packageInstallPath;

    public LastKnownGoodStartupRecoveryServiceStoreLockTests()
    {
        _stateFilePath = Path.Combine(_root.Path, "store-state.json");
        _packageInstallPath = Path.Combine(_root.Path, "pkg-a", "1.0.0");
        Directory.CreateDirectory(_packageInstallPath);
    }

    public void Dispose() => _root.Dispose();

    [Fact]
    public async Task TryRecoverAsync_WhenAnotherHandleHoldsTheLock_LeavesStateFileByteIdenticalAndReportsSkip()
    {
        await SeedValidLastKnownGoodStateAsync();
        var bytesBefore = await File.ReadAllBytesAsync(_stateFilePath);

        using var heldElsewhere = CreateStoreLock().Acquire();
        var dispatcher = new WritingObserverDispatcher(CreateStoreRegistry());
        var recovery = CreateRecoveryService(dispatcher, CreateStoreLock());

        var result = await recovery.TryRecoverAsync(CorrelationId, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(LastKnownGoodStartupRecoveryResult.StoreLockUnavailableReason, result.Reason);
        Assert.False(dispatcher.Invoked);
        Assert.Equal(bytesBefore, await File.ReadAllBytesAsync(_stateFilePath));
    }

    [Fact]
    public async Task TryRecoverAsync_WhenStoreLockIsAvailable_RunsAndCanStillWrite()
    {
        await SeedValidLastKnownGoodStateAsync();

        var dispatcher = new WritingObserverDispatcher(CreateStoreRegistry());
        var recovery = CreateRecoveryService(dispatcher, CreateStoreLock());

        var result = await recovery.TryRecoverAsync(CorrelationId, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(dispatcher.Invoked);
        var stateAfter = await CreateStoreRegistry().GetStateAsync(CancellationToken.None);
        Assert.True(stateAfter.LastFailureById.ContainsKey("pkg-a"));
    }

    [Fact]
    public async Task TryRecoverAsync_WhenStoreLockIsDisabled_WritesEvenWhileAnotherHolderOwnsTheStore()
    {
        await SeedValidLastKnownGoodStateAsync();

        using var heldElsewhere = CreateStoreLock().Acquire();
        var dispatcher = new WritingObserverDispatcher(CreateStoreRegistry());
        var recovery = CreateRecoveryService(dispatcher, CreateStoreLock(enableStoreLock: false));

        var result = await recovery.TryRecoverAsync(CorrelationId, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(dispatcher.Invoked);
        var stateAfter = await CreateStoreRegistry().GetStateAsync(CancellationToken.None);
        Assert.True(stateAfter.LastFailureById.ContainsKey("pkg-a"));
    }

    private async Task SeedValidLastKnownGoodStateAsync()
    {
        var store = CreateStoreRegistry();
        await store.PersistActiveVersionsAsync(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["pkg-a"] = "1.0.0" },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["pkg-a"] = "1.0.0" },
            "corr-seed",
            CancellationToken.None,
            new Dictionary<string, ActivePackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a"] = new(
                    "pkg-a",
                    "1.0.0",
                    "local-cache",
                    "desired-source",
                    _packageInstallPath,
                    DateTimeOffset.UtcNow,
                    "corr-seed")
            });
    }

    private LastKnownGoodStartupRecoveryService CreateRecoveryService(IObserverEventDispatcher dispatcher, IStoreLock storeLock) =>
        new(CreateStoreRegistry(), dispatcher, new StartupRecoveryState(), cycleFailureContributors: null, storeLock);

    private StoreRegistry CreateStoreRegistry() => new(new StoreStateSerializer(), _stateFilePath);

    private StoreLock CreateStoreLock(bool enableStoreLock = true) =>
        new(
            EffectiveStorePersistenceSettings.Resolve(new() { StateFilePath = _stateFilePath }),
            new OptionsWrapper<ReconciliationOptions>(new() { EnableStoreLock = enableStoreLock }),
            NullLogger<StoreLock>.Instance);

    /// <summary>
    /// Stands in for the transitive write a load failure produces during
    /// <c>IObserverEventDispatcher.PublishReconciledAsync</c> in production — a failed load records
    /// through <c>IFailureRecorder</c> into the store — without pulling in the loading module.
    /// </summary>
    private sealed class WritingObserverDispatcher(IStoreRegistry storeRegistry) : IObserverEventDispatcher
    {
        public bool Invoked { get; private set; }

        public Task PublishChangingAsync(PackageChangeSet changeSet, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PublishChangedAsync(PackageChangeSet changeSet, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task NotifyPackageFailedAsync(
            string packageId,
            Exception exception,
            string correlationId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task PublishReconciledAsync(
            PackageChangeSet changeSet,
            IReadOnlyList<ResolvedPackage> appliedPackages,
            CancellationToken cancellationToken)
        {
            Invoked = true;
            await storeRegistry.PersistFailureAsync("pkg-a", "load", "boom", changeSet.CorrelationId, cancellationToken);
        }
    }
}
