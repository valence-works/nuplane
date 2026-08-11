using Microsoft.Extensions.Time.Testing;

namespace Nuplane.Sources.Directory.Tests.TestSupport;

/// <summary>
/// Drives a <see cref="FakeTimeProvider" /> forward so timer-based production code can be exercised without
/// depending on the wall clock.
/// </summary>
/// <remarks>
/// Virtual time only moves when a test moves it, so a loaded machine can never make an assertion fire early.
/// The small real delays below are scheduling nudges rather than deadlines: if a continuation misses one, the
/// only consequence is another advance, never a failure.
/// </remarks>
public static class FakeClockDriver
{
    private static readonly TimeSpan ContinuationNudge = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Advances virtual time by <paramref name="amount" /> and gives parked continuations a chance to run.
    /// </summary>
    public static async Task AdvanceAsync(FakeTimeProvider time, TimeSpan amount)
    {
        ArgumentNullException.ThrowIfNull(time);

        time.Advance(amount);
        await Task.Delay(ContinuationNudge, CancellationToken.None);
    }

    /// <summary>
    /// Advances virtual time in <paramref name="step" /> increments until <paramref name="task" /> completes,
    /// giving up after <paramref name="maxSteps" /> increments.
    /// </summary>
    /// <exception cref="TimeoutException">The task did not complete within the virtual-time budget.</exception>
    public static async Task AdvanceUntilCompletedAsync(
        FakeTimeProvider time,
        Task task,
        TimeSpan step,
        int maxSteps = 20)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(task);

        for (var taken = 0; !task.IsCompleted; taken++)
        {
            if (taken == maxSteps)
            {
                throw new TimeoutException(
                    $"Task did not complete within {maxSteps} virtual steps of {step.TotalMilliseconds}ms.");
            }

            time.Advance(step);
            await Task.WhenAny(task, Task.Delay(ContinuationNudge, CancellationToken.None));
        }

        await task;
    }
}
