namespace Nuplane.Runtime.Tests.TestSupport;

/// <summary>
/// Assertion helpers for verifying debounce/coalescing behavior in tests.
/// </summary>
public static class DebounceAssert
{
    /// <summary>
    /// Waits until <paramref name="predicate"/> returns <c>true</c>, polling at <paramref name="pollInterval"/>.
    /// Throws <see cref="TimeoutException"/> if the predicate does not become true within <paramref name="timeout"/>.
    /// </summary>
    public static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        string? message = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        var poll = pollInterval ?? TimeSpan.FromMilliseconds(50);
        using var cts = new CancellationTokenSource(timeout);

        while (!predicate())
        {
            if (cts.IsCancellationRequested)
            {
                throw new TimeoutException(
                    message ?? $"Predicate did not become true within {timeout.TotalMilliseconds}ms.");
            }

            await Task.Delay(poll, CancellationToken.None);
        }
    }

    /// <summary>
    /// Asserts that <paramref name="counter"/> does not exceed <paramref name="maxCount"/>
    /// within the specified <paramref name="observation"/> window, proving coalescing.
    /// </summary>
    public static async Task AssertCoalescedAsync(
        Func<int> counter,
        int maxCount,
        TimeSpan observation,
        string? message = null)
    {
        ArgumentNullException.ThrowIfNull(counter);

        await Task.Delay(observation, CancellationToken.None);

        var actual = counter();
        if (actual > maxCount)
        {
            throw new Xunit.Sdk.XunitException(
                message ?? $"Expected at most {maxCount} invocations within {observation.TotalMilliseconds}ms, but observed {actual}.");
        }
    }

    /// <summary>
    /// Asserts that a count function reaches the expected value within the timeout.
    /// </summary>
    public static async Task WaitForCountAsync(
        Func<int> counter,
        int expected,
        TimeSpan timeout,
        string? message = null)
    {
        ArgumentNullException.ThrowIfNull(counter);

        using var cts = new CancellationTokenSource(timeout);
        while (counter() < expected)
        {
            if (cts.IsCancellationRequested)
            {
                throw new TimeoutException(
                    message ?? $"Counter did not reach {expected} within {timeout.TotalMilliseconds}ms. Current: {counter()}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), CancellationToken.None);
        }
    }
}
