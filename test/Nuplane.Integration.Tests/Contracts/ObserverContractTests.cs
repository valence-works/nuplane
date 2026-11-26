using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Observability;
using Nuplane.Reconciliation.Models;
using NSubstitute;

namespace Nuplane.Integration.Tests.Contracts;

public sealed class ObserverContractTests
{
    [Fact]
    public async Task TriggerManualAsync_FiresObserverCallbacksInOrder_WithSharedCorrelationId()
    {
        var observer = new RecordingObserver();

        var service = ReconciliationServiceFactory.Create(
            sources: [new StaticSource([new("pkg-a", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "source-a")])],
            observerEventDispatcher: new ObserverEventDispatcher([observer]));

        var result = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.Equal(["Changing", "Changed"], observer.Events);
        Assert.NotNull(observer.ChangingCorrelationId);
        Assert.NotNull(observer.ChangedCorrelationId);
        Assert.Equal(observer.ChangingCorrelationId, observer.ChangedCorrelationId);
    }

    [Fact]
    public async Task OnPackagesChanged_ObserverImplementsOneCallback_DispatchesAllEventsWithoutErrors()
    {
        var services = new ServiceCollection();
        services.AddNuplane(builder => builder.OnPackagesChanged<ChangedObserver>());
        var logger = Substitute.For<IReconciliationLogger>();
        services.AddSingleton(logger);
        await using var provider = services.BuildServiceProvider();
        var observer = Assert.IsType<ChangedObserver>(Assert.Single(provider.GetServices<INuplaneObserver>()));
        var dispatcher = provider.GetRequiredService<IObserverEventDispatcher>();
        var changeSet = new PackageChangeSet([], [], [], "single-callback", DateTimeOffset.UnixEpoch);

        await dispatcher.PublishChangingAsync(changeSet, CancellationToken.None);
        await dispatcher.PublishChangedAsync(changeSet, CancellationToken.None);
        await dispatcher.NotifyPackageFailedAsync("pkg-a", new IOException("package failure"), changeSet.CorrelationId,
            CancellationToken.None);
        await dispatcher.PublishReconciledAsync(changeSet, [], CancellationToken.None);

        Assert.Same(changeSet, observer.Received);
        logger.DidNotReceiveWithAnyArgs().LogObserverError(default!, default!, default!);
    }

    private sealed class ChangedObserver : INuplaneObserver
    {
        public PackageChangeSet? Received { get; private set; }

        public Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken cancellationToken)
        {
            Received = changeSet;
            return Task.CompletedTask;
        }
    }

    private sealed class StaticSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) => Task.FromResult(requests);
    }

    private sealed class RecordingObserver : INuplaneObserver
    {
        public List<string> Events { get; } = [];
        public string? ChangingCorrelationId { get; private set; }
        public string? ChangedCorrelationId { get; private set; }

        public Task OnPackagesChangingAsync(PackageChangeSet changeSet, CancellationToken ct)
        {
            Events.Add("Changing");
            ChangingCorrelationId = changeSet.CorrelationId;
            return Task.CompletedTask;
        }

        public Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken ct)
        {
            Events.Add("Changed");
            ChangedCorrelationId = changeSet.CorrelationId;
            return Task.CompletedTask;
        }

        public Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken ct)
        {
            Events.Add("Failed");
            return Task.CompletedTask;
        }
    }
}