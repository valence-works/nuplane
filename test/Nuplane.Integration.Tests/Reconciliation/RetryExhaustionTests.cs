using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class RetryExhaustionTests
{
    [Fact]
    public async Task ManualTrigger_WhenRetriesExhausted_StopsAtConfiguredBound()
    {
        var source = new AlwaysFailingSource();
        var options = new ReconciliationOptions
        {
            MaxRetryAttempts = 2,
            InitialRetryBackoff = TimeSpan.FromMilliseconds(1),
            MaxRetryBackoff = TimeSpan.FromMilliseconds(2)
        };

        var service = ReconciliationServiceFactory.Create(
            sources: [source],
            sourceTrustOptions: new() { AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "pkg-a" } },
            packageResolver: new NuGetPackageResolver(),
            reconciliationOptions: options);

        var result = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        Assert.True(result.IsDegraded);
        Assert.Empty(result.ChangeSet.Added);
        Assert.Equal(3, source.Attempts);
    }

    private sealed class AlwaysFailingSource : IDesiredPackageSource
    {
        public int Attempts { get; private set; }

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
        {
            Attempts++;
            throw new InvalidOperationException("still unavailable");
        }
    }
}
