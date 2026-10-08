using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class RemovalCompletionIntegrationTests
{
    [Fact]
    public async Task TriggerAsync_RemovesFinalActivePackage_PersistsEmptyStateBeforeRegisteredCompletionObserver()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nuplane-removal-completion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var stateFilePath = Path.Combine(tempRoot, "state.json");
            var serializer = new StoreStateSerializer();
            var store = new StoreRegistry(serializer, stateFilePath);
            var activeVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-removed"] = "1.0.0"
            };
            await store.PersistActiveVersionsAsync(activeVersions, activeVersions, "seed-active", CancellationToken.None);

            var observer = new PersistedStateObserver(serializer, stateFilePath);
            var dispatcher = new ObserverEventDispatcher([observer]);
            var service = ReconciliationServiceFactory.Create(
                sources: [],
                storeRegistry: store,
                observerEventDispatcher: dispatcher);

            var result = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);
            var persistedAfterCycle = await serializer.LoadAsync(stateFilePath, CancellationToken.None);

            Assert.False(result.Skipped);
            Assert.False(result.IsDegraded);
            Assert.Equal(["pkg-removed"], result.ChangeSet.Removed);
            Assert.Empty(result.FailedPackages);
            Assert.Equal(["changed", "reconciled"], observer.Calls);
            Assert.Same(result.ChangeSet, observer.ChangeSet);
            Assert.NotNull(observer.AppliedPackages);
            Assert.Empty(observer.AppliedPackages);
            Assert.NotNull(observer.ActiveVersionsSeenAtCompletion);
            Assert.Empty(observer.ActiveVersionsSeenAtCompletion);
            Assert.Empty(persistedAfterCycle.ActiveVersionById);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class PersistedStateObserver(IStoreStateSerializer serializer, string stateFilePath) : INuplaneObserver
    {
        public List<string> Calls { get; } = [];
        public PackageChangeSet? ChangeSet { get; private set; }
        public IReadOnlyList<ResolvedPackage>? AppliedPackages { get; private set; }
        public IReadOnlyDictionary<string, string>? ActiveVersionsSeenAtCompletion { get; private set; }

        public Task OnPackagesChangingAsync(PackageChangeSet changeSet, CancellationToken ct) => Task.CompletedTask;

        public Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken ct)
        {
            Calls.Add("changed");
            return Task.CompletedTask;
        }

        public Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken ct) => Task.CompletedTask;

        public async Task OnPackagesReconciledAsync(
            PackageChangeSet changeSet,
            IReadOnlyList<ResolvedPackage> appliedPackages,
            CancellationToken ct)
        {
            Calls.Add("reconciled");
            ChangeSet = changeSet;
            AppliedPackages = appliedPackages;
            var persisted = await serializer.LoadAsync(stateFilePath, ct);
            ActiveVersionsSeenAtCompletion = persisted.ActiveVersionById;
        }
    }
}
