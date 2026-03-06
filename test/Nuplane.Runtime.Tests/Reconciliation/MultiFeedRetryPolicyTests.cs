using Microsoft.Extensions.Options;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class MultiFeedRetryPolicyTests
{
    [Fact]
    public async Task ExecuteAsync_StopsAtMaxAttempts()
    {
        var options = new ReconciliationOptions
        {
            MaxRetryAttempts = 2,
            InitialRetryBackoff = TimeSpan.FromMilliseconds(1),
            MaxRetryBackoff = TimeSpan.FromMilliseconds(2)
        };

        var policy = new ReconciliationRetryPolicy(new OptionsWrapper<ReconciliationOptions>(options));
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            policy.ExecuteAsync<int>(
                _ =>
                {
                    attempts++;
                    throw new InvalidOperationException("feed unavailable");
                },
                CancellationToken.None));

        Assert.Equal(3, attempts);
    }

    [Fact]
    public void GetBackoffForRetry_ProgressesExponentiallyWithinBounds()
    {
        var options = new ReconciliationOptions
        {
            InitialRetryBackoff = TimeSpan.FromMilliseconds(5),
            MaxRetryBackoff = TimeSpan.FromMilliseconds(20)
        };

        Assert.Equal(TimeSpan.FromMilliseconds(5), ReconciliationRetryPolicy.GetBackoffForRetry(options, 1));
        Assert.Equal(TimeSpan.FromMilliseconds(10), ReconciliationRetryPolicy.GetBackoffForRetry(options, 2));
        Assert.Equal(TimeSpan.FromMilliseconds(20), ReconciliationRetryPolicy.GetBackoffForRetry(options, 3));
    }
}
