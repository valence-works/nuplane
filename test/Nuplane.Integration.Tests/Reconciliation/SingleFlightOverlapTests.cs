using Nuplane.Abstractions;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.Models;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class SingleFlightOverlapTests
{
    [Fact]
    public async Task OverlappingManualTriggers_SkipSecondRunWhenSingleFlightEnabled()
    {
        var source = new StaticSource([
            new("pkg-a", "1.2.3", "feed-1", PackageUpdatePolicy.Exact, "source-a")
        ]);

        var service = ReconciliationServiceFactory.Create(
            sources: [source],
            sourceTrustOptions: new() { AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "pkg-a" } },
            packageResolver: new SlowResolver(TimeSpan.FromMilliseconds(200)),
            reconciliationOptions: new() { EnableSingleFlight = true });

        var firstRun = service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);
        await Task.Delay(30);
        var secondRun = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);
        var firstResult = await firstRun;

        Assert.False(firstResult.Skipped);
        Assert.True(secondRun.Skipped);
    }

    private sealed class StaticSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) => Task.FromResult(requests);
    }

    private sealed class SlowResolver(TimeSpan delay) : INuGetPackageResolver
    {
        public async Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new(request.Id, request.VersionRange, request.FeedName ?? "default", $"/tmp/{request.Id}/{request.VersionRange}", DateTimeOffset.UtcNow);
        }
    }
}
