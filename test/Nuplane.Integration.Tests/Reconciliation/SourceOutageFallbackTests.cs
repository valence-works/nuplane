using Nuplane.Abstractions;
using Nuplane.NuGet.Resolution;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class SourceOutageFallbackTests
{
    [Fact]
    public async Task ManualTrigger_WhenSourceOutage_UsesLastSuccessfulSnapshot()
    {
        var source = new FlakySource();
        var service = CreateService(source, new ReconciliationOptions { MaxRetryAttempts = 1 });

        var first = await service.TriggerManualAsync(CancellationToken.None);
        var second = await service.TriggerManualAsync(CancellationToken.None);

        Assert.False(first.IsDegraded);
        Assert.True(second.IsDegraded);
        Assert.Empty(second.FailedPackages);
        Assert.Empty(second.ChangeSet.Removed);
        Assert.Empty(second.ChangeSet.Updated);
    }

    private static ReconciliationService CreateService(IDesiredPackageSource source, ReconciliationOptions options)
    {
        return new ReconciliationService(
            new[] { source },
            new SourceTrustOptions { AllowedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pkg-a" } },
            new DesiredStateAggregator(),
            new DesiredActualDiffEngine(),
            new NuGetPackageResolver(),
            new StoreRegistry(new StoreStateSerializer(), stateFilePath: null),
            options);
    }

    private sealed class FlakySource : IDesiredPackageSource
    {
        private int calls;

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
        {
            calls++;
            if (calls >= 2)
            {
                throw new InvalidOperationException("source unavailable");
            }

            return Task.FromResult<IReadOnlyList<PackageRequest>>(
                [new PackageRequest("pkg-a", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "source-a")]);
        }
    }
}
