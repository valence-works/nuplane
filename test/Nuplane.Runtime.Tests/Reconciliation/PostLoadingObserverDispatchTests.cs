using NSubstitute;
using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Reconciliation.Models;
using Nuplane.Runtime.Tests;
using Nuplane.Store.Cleanup;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class PostLoadingObserverDispatchTests
{
    [Fact]
    public async Task OrdinaryReconciliation_PublishesNormalCompletionWithoutPostLoadingCallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-post-loading-ordinary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var statePath = Path.Combine(root, "store-state.json");
            var serializer = new StoreStateSerializer();
            await serializer.SaveAsync(statePath, StoreStateRecord.Empty() with
            {
                ActiveVersionById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Retired.Package"] = "1.0.0"
                }
            }, CancellationToken.None);

            var dispatcher = new PostLoadingDispatcherProbe();
            var cleanup = Substitute.For<IPackageCleanupService>();
            cleanup.ExecuteAutomaticAsync(default!, default!, default!, default, default)
                .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<CleanupDecision>>([]));
            var service = ReconciliationServiceFactory.Create(
                storeRegistry: new StoreRegistry(serializer, statePath),
                observerEventDispatcher: dispatcher,
                packageCleanupService: cleanup);

            var result = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

            Assert.Single(result.ChangeSet.Removed);
            Assert.Equal(1, dispatcher.ReconciledCount);
            Assert.Equal(0, dispatcher.PostLoadingCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class PostLoadingDispatcherProbe : IPostLoadingObserverEventDispatcher
    {
        public int ReconciledCount { get; private set; }
        public int PostLoadingCount { get; private set; }
        public bool HasPostLoadingObservers => true;

        public Task PublishChangingAsync(PackageChangeSet changeSet, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PublishChangedAsync(PackageChangeSet changeSet, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NotifyPackageFailedAsync(string packageId, Exception exception, string correlationId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PublishReconciledAsync(PackageChangeSet changeSet, IReadOnlyList<ResolvedPackage> appliedPackages,
            CancellationToken cancellationToken)
        {
            ReconciledCount++;
            return Task.CompletedTask;
        }

        public Task PublishPackageLoadingCompletedAsync(PackageChangeSet changeSet,
            IReadOnlyList<ResolvedPackage> appliedPackages, CancellationToken cancellationToken)
        {
            PostLoadingCount++;
            return Task.CompletedTask;
        }
    }
}
