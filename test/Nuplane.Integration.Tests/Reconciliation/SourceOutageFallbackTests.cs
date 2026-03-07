using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class SourceOutageFallbackTests
{
    [Fact]
    public async Task ManualTrigger_WhenSourceOutage_UsesLastSuccessfulSnapshot()
    {
        var source = new FlakySource();
        var service = CreateService(source, new() { MaxRetryAttempts = 1 });

        var first = await service.TriggerAsync(new(Nuplane.Runtime.Reconciliation.Models.TriggerType.Manual), CancellationToken.None);
        var second = await service.TriggerAsync(new(Nuplane.Runtime.Reconciliation.Models.TriggerType.Manual), CancellationToken.None);

        Assert.False(first.IsDegraded);
        Assert.True(second.IsDegraded);
        Assert.Empty(second.FailedPackages);
        Assert.Empty(second.ChangeSet.Removed);
        Assert.Empty(second.ChangeSet.Updated);
    }

    private static ReconciliationService CreateService(IDesiredPackageSource source, ReconciliationOptions options)
    {
        return ReconciliationServiceFactory.Create(
            sources: [source],
            sourceTrustOptions: new() { AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "pkg-a" } },
            packageResolver: new NuGetPackageResolver(),
            reconciliationOptions: options);
    }

    private sealed class FlakySource : IDesiredPackageSource
    {
        private int _calls;

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
        {
            _calls++;
            if (_calls >= 2)
            {
                throw new InvalidOperationException("source unavailable");
            }

            return Task.FromResult<IReadOnlyList<PackageRequest>>(
                [new("pkg-a", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "source-a")]);
        }
    }
}
