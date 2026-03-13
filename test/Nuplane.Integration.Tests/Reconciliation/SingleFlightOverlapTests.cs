using Nuplane.Abstractions;
using Nuplane.Feeds;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class SingleFlightOverlapTests
{
    [Fact]
    public async Task OverlappingManualTriggers_SkipSecondRunWhenSingleFlightEnabled()
    {
        var source = new StaticSource([
            new("pkg-a", "1.2.3", "feed-1", PackageUpdatePolicy.Exact, "source-a")
        ]);
        var resolver = new CoordinatedResolver();

        var service = ReconciliationServiceFactory.Create(
            sources: [source],
            packageResolver: resolver,
            reconciliationOptions: new() { EnableSingleFlight = true });

        var firstRun = service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);
        await resolver.WaitUntilStartedAsync();
        var secondRun = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);
        resolver.Release();
        var firstResult = await firstRun;

        Assert.False(firstResult.Skipped);
        Assert.True(secondRun.Skipped);
    }

    private sealed class StaticSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) => Task.FromResult(requests);
    }

    private sealed class CoordinatedResolver : INuGetPackageResolver
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilStartedAsync() => _started.Task;

        public void Release() => _release.TrySetResult();

        public async Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new(request.Id, request.VersionRange, request.FeedName ?? "default", $"/tmp/{request.Id}/{request.VersionRange}", DateTimeOffset.UtcNow);
        }
    }
}
