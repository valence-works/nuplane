using Nuplane.Abstractions;
using Nuplane.NuGet.Resolution;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class DesiredStateReconciliationTests
{
    [Fact]
    public async Task ManualTrigger_RepeatedRun_IsIdempotentOnSecondCycle()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var source = new StaticSource(new[]
        {
            new PackageRequest("pkg-a", "1.2.3", "feed-1", PackageUpdatePolicy.Exact, "source-a")
        });

        var service = new ReconciliationService(
            new[] { source },
            new() { AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "pkg-a" } },
            new(),
            new(),
            new NuGetPackageResolver(),
            new(new(), stateFilePath: null),
            new());

        var first = await service.TriggerManualAsync(CancellationToken.None);
        var second = await service.TriggerManualAsync(CancellationToken.None);

        Assert.False(first.Skipped);
        Assert.Single(first.ChangeSet.Added);
        Assert.Empty(second.ChangeSet.Added);
        Assert.Empty(second.ChangeSet.Updated);
        Assert.Empty(second.ChangeSet.Removed);
    }

    private sealed class StaticSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) => Task.FromResult(requests);
    }
}
