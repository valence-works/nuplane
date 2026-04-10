using Nuplane.Abstractions;
using Nuplane.Observability;
using Nuplane.Operational;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class ActivePackageCatalogConsistencyIntegrationTests
{
    [Fact]
    public async Task GetSnapshotAsync_DuringPersist_WaitsForAtomicDescriptorAndVersionUpdate()
    {
        var serializer = new BlockingStoreStateSerializer();
        var store = new StoreRegistry(serializer, Path.Combine(Path.GetTempPath(), $"nuplane-active-catalog-{Guid.NewGuid():N}.json"));

        await store.PersistActiveVersionsAsync(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a"] = "1.0.0"
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a"] = "1.0.0"
            },
            "corr-initial",
            CancellationToken.None,
            new Dictionary<string, ActivePackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a"] = CreateDescriptor("pkg-a", "1.0.0", "/packages/pkg-a/1.0.0", "corr-initial")
            });

        var catalog = CreateCatalog(store);
        var initialSnapshot = await catalog.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal("1.0.0", Assert.Single(initialSnapshot.Packages).Version);

        serializer.BlockNextSave();

        var persistTask = store.PersistActiveVersionsAsync(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a"] = "2.0.0"
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a"] = "2.0.0"
            },
            "corr-updated",
            CancellationToken.None,
            new Dictionary<string, ActivePackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a"] = CreateDescriptor("pkg-a", "2.0.0", "/packages/pkg-a/2.0.0", "corr-updated")
            });

        await serializer.WaitForSaveToStartAsync();

        var readWhilePersistingTask = catalog.GetSnapshotAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.False(readWhilePersistingTask.IsCompleted, "Read should wait until the store write completes so no partial package/version state leaks through.");

        serializer.ReleaseSave();
        await persistTask;

        var updatedSnapshot = await readWhilePersistingTask;
        var descriptor = Assert.Single(updatedSnapshot.Packages);
        Assert.Equal("pkg-a", descriptor.PackageId);
        Assert.Equal("2.0.0", descriptor.Version);
        Assert.Equal("/packages/pkg-a/2.0.0", descriptor.InstallPath);
        Assert.Equal("corr-updated", descriptor.ActivationCorrelationId);
    }

    private static ActivePackageDescriptor CreateDescriptor(string id, string version, string installPath, string correlationId) =>
        new(id, version, "feed-a", "source-a", installPath, DateTimeOffset.UtcNow, correlationId);

    private static ActivePackageCatalog CreateCatalog(IStoreRegistry storeRegistry) =>
        new(storeRegistry, new ReconciliationLogger(), new ReconciliationMetrics(new ReconciliationTelemetry()));

    private sealed class BlockingStoreStateSerializer : IStoreStateSerializer
    {
        private StoreStateRecord _state = StoreStateRecord.Empty();
        private TaskCompletionSource _saveStarted = NewSignal();
        private TaskCompletionSource _releaseSave = NewSignal();
        private bool _blockNextSave;

        public Task<StoreStateRecord> LoadAsync(string stateFilePath, CancellationToken cancellationToken) => Task.FromResult(_state);

        public async Task SaveAsync(string stateFilePath, StoreStateRecord state, CancellationToken cancellationToken)
        {
            if (_blockNextSave)
            {
                _saveStarted.TrySetResult();
                await _releaseSave.Task.WaitAsync(cancellationToken);
                _blockNextSave = false;
            }

            _state = state;
        }

        public void BlockNextSave()
        {
            _blockNextSave = true;
            _saveStarted = NewSignal();
            _releaseSave = NewSignal();
        }

        public Task WaitForSaveToStartAsync() => _saveStarted.Task;

        public void ReleaseSave() => _releaseSave.TrySetResult();

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

