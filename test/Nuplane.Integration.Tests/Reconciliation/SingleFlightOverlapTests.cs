using Nuplane.Abstractions;
using Nuplane.NuGet.Resolution;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class SingleFlightOverlapTests
{
    [Fact]
    public async Task OverlappingManualTriggers_SkipSecondRunWhenSingleFlightEnabled()
    {
        var source = new StaticSource(new[]
        {
            new PackageRequest("pkg-a", "1.2.3", "feed-1", PackageUpdatePolicy.Exact, "source-a")
        });

        var service = new ReconciliationService(
            new[] { source },
            new() { AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "pkg-a" } },
            new(),
            new(),
            new SlowResolver(TimeSpan.FromMilliseconds(200)),
            new(new(), stateFilePath: null),
            new() { EnableSingleFlight = true });

        var firstRun = service.TriggerManualAsync(CancellationToken.None);
        await Task.Delay(30);
        var secondRun = await service.TriggerManualAsync(CancellationToken.None);
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
