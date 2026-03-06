using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nuplane.Loading;
using Nuplane.Loading.Events;
using Nuplane.Loading.Hosting;

namespace Nuplane.Loading.Tests;

/// <summary>
/// T014 — Verifies <see cref="LoadingEventDispatcher"/> fans out events to all
/// registered <see cref="IPackageLoadingObserver"/> instances with per-observer isolation.
/// </summary>
public sealed class LoadingEventDispatcherTests
{
    [Fact]
    public async Task AllObservers_ReceiveOnPackagesLoaded()
    {
        // Arrange
        var obs1 = new SpyObserver();
        var obs2 = new SpyObserver();
        var sut = new LoadingEventDispatcher(
            [obs1, obs2],
            NullLogger<LoadingEventDispatcher>.Instance);

        var evt = CreateEvent();

        // Act
        await sut.PublishLoadedAsync(evt, CancellationToken.None);

        // Assert
        Assert.Single(obs1.ReceivedEvents);
        Assert.Single(obs2.ReceivedEvents);
        Assert.Same(evt, obs1.ReceivedEvents[0]);
        Assert.Same(evt, obs2.ReceivedEvents[0]);
    }

    [Fact]
    public async Task ObserverException_IsCaughtAndOtherObserversStillCalled()
    {
        // Arrange
        var throwingObs = new ThrowingObserver();
        var goodObs = new SpyObserver();
        var sut = new LoadingEventDispatcher(
            [throwingObs, goodObs],
            NullLogger<LoadingEventDispatcher>.Instance);

        var evt = CreateEvent();

        // Act — should not throw
        await sut.PublishLoadedAsync(evt, CancellationToken.None);

        // Assert — the good observer was still called
        Assert.Single(goodObs.ReceivedEvents);
    }

    [Fact]
    public async Task NoObservers_NoError()
    {
        // Arrange
        var sut = new LoadingEventDispatcher(
            [],
            NullLogger<LoadingEventDispatcher>.Instance);

        var evt = CreateEvent();

        // Act & Assert — no exception
        await sut.PublishLoadedAsync(evt, CancellationToken.None);
    }

    [Fact]
    public async Task PublishFailed_CallsOnPackageLoadFailedAsync_WithIsolation()
    {
        // Arrange
        var throwingObs = new ThrowingOnFailureObserver();
        var goodObs = new SpyObserver();
        var sut = new LoadingEventDispatcher(
            [throwingObs, goodObs],
            NullLogger<LoadingEventDispatcher>.Instance);

        // Act — should not throw
        await sut.PublishFailedAsync("pkg-a", "load error", CancellationToken.None);

        // Assert — good observer received the failure
        Assert.Single(goodObs.ReceivedFailures);
        Assert.Equal("pkg-a", goodObs.ReceivedFailures[0].packageId);
        Assert.Equal("load error", goodObs.ReceivedFailures[0].reason);
    }

    #region Helpers

    private static PackageLoadedEvent CreateEvent() =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow,
            [new PackageLoadSession("pkg-a", "1.0.0", "/install", "ctx-a", DateTimeOffset.UtcNow, true, null)]);

    private sealed class SpyObserver : IPackageLoadingObserver
    {
        public List<PackageLoadedEvent> ReceivedEvents { get; } = [];
        public List<(string packageId, string reason)> ReceivedFailures { get; } = [];

        public Task OnPackagesLoadedAsync(PackageLoadedEvent evt, CancellationToken cancellationToken)
        {
            ReceivedEvents.Add(evt);
            return Task.CompletedTask;
        }

        public Task OnPackageLoadFailedAsync(string packageId, string reason, CancellationToken cancellationToken)
        {
            ReceivedFailures.Add((packageId, reason));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingObserver : IPackageLoadingObserver
    {
        public Task OnPackagesLoadedAsync(PackageLoadedEvent evt, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Observer boom");
    }

    private sealed class ThrowingOnFailureObserver : IPackageLoadingObserver
    {
        public Task OnPackageLoadFailedAsync(string packageId, string reason, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Failure observer boom");
    }

    #endregion
}
