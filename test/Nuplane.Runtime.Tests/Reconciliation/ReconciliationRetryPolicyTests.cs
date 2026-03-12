using Microsoft.Extensions.Options;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Configuration;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class ReconciliationRetryPolicyTests
{
    [Fact]
    public async Task ExecuteAsync_RetriesUntilSuccess_WithinMaxAttempts()
    {
        var policy = new ReconciliationRetryPolicy(
            new OptionsWrapper<ReconciliationOptions>(new()
            {
                MaxRetryAttempts = 3,
                InitialRetryBackoff = TimeSpan.FromMilliseconds(1),
                MaxRetryBackoff = TimeSpan.FromMilliseconds(4)
            }));

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
    public async Task ExecuteAsync_DoesNotRetryOperationCanceledException()
    {
        var policy = new ReconciliationRetryPolicy(
            new OptionsWrapper<ReconciliationOptions>(new()
            {
                MaxRetryAttempts = 3,
                InitialRetryBackoff = TimeSpan.FromMilliseconds(1),
                MaxRetryBackoff = TimeSpan.FromMilliseconds(4)
            }));

        var attempts = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            policy.ExecuteAsync<int>(
                _ =>
                {
                    attempts++;
                    throw new OperationCanceledException();
                },
                CancellationToken.None));

        Assert.Equal(1, attempts);
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
