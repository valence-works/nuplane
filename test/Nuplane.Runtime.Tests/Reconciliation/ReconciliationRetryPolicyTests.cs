using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class ReconciliationRetryPolicyTests
{
    [Fact]
    public async Task ExecuteAsync_RetriesUntilSuccess_WithinMaxAttempts()
    {
        var policy = new ReconciliationRetryPolicy(
            new()
            {
                MaxRetryAttempts = 3,
                InitialRetryBackoff = TimeSpan.FromMilliseconds(1),
                MaxRetryBackoff = TimeSpan.FromMilliseconds(4)
            });

        var attempts = 0;
        var result = await policy.ExecuteAsync(
            _ =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new InvalidOperationException("retry");
                }

                return Task.FromResult("ok");
            },
            CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void GetBackoffForRetry_UsesExponentialProgression_ClampedToMax()
    {
        var options = new ReconciliationOptions
        {
            MaxRetryAttempts = 5,
            InitialRetryBackoff = TimeSpan.FromMilliseconds(10),
            MaxRetryBackoff = TimeSpan.FromMilliseconds(25)
        };

        Assert.Equal(TimeSpan.FromMilliseconds(10), ReconciliationRetryPolicy.GetBackoffForRetry(options, 1));
        Assert.Equal(TimeSpan.FromMilliseconds(20), ReconciliationRetryPolicy.GetBackoffForRetry(options, 2));
        Assert.Equal(TimeSpan.FromMilliseconds(25), ReconciliationRetryPolicy.GetBackoffForRetry(options, 3));
        Assert.Equal(TimeSpan.FromMilliseconds(25), ReconciliationRetryPolicy.GetBackoffForRetry(options, 4));
    }
}
