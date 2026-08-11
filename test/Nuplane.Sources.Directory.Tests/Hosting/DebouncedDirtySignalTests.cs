using Microsoft.Extensions.Time.Testing;
using Nuplane.Sources.Directory.Hosting;
using Nuplane.Sources.Directory.Tests.TestSupport;

namespace Nuplane.Sources.Directory.Tests.Hosting;

/// <summary>
/// Contract tests for the debounced dirty-signal primitive used by directory observation.
/// </summary>
/// <remarks>
/// The debounce window is measured against a <see cref="FakeTimeProvider" />, so every timing assertion here is
/// about virtual time the test controls rather than about how quickly the machine happens to be running.
/// </remarks>
public sealed class DebouncedDirtySignalTests
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(150);

    private readonly FakeTimeProvider time = new();
    private readonly DebouncedDirtySignal signal;

    public DebouncedDirtySignalTests()
    {
        signal = new DebouncedDirtySignal(DebounceWindow, time);
    }

    [Fact]
    public async Task BurstSignals_AreCoalesced_IntoASingleSettledWakeup()
    {
        // Arrange
        var wakeup = signal.WaitForNextSettledSignalAsync(CancellationToken.None);

        // Act: a burst of signals inside a single quiet window.
        for (var i = 0; i < 10; i++)
        {
            signal.Signal();
        }

        await FakeClockDriver.AdvanceUntilCompletedAsync(time, wakeup, DebounceWindow);

        // Assert: the burst produced exactly one wakeup, so a second wait never settles.
        using var cts = new CancellationTokenSource();
        var leftover = signal.WaitForNextSettledSignalAsync(cts.Token);

        await FakeClockDriver.AdvanceAsync(time, DebounceWindow * 10);
        Assert.False(leftover.IsCompleted);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leftover);
    }

    [Fact]
    public async Task SignalDuringDebounce_ExtendsTheQuietWindow()
    {
        // Arrange
        var wakeup = signal.WaitForNextSettledSignalAsync(CancellationToken.None);
        signal.Signal();

        // Act: signal again halfway through the quiet window.
        await FakeClockDriver.AdvanceAsync(time, DebounceWindow / 2);
        Assert.False(wakeup.IsCompleted);
        signal.Signal();

        // Assert: the original deadline passes without settling, because the late signal restarted the window.
        await FakeClockDriver.AdvanceAsync(time, DebounceWindow / 2);
        Assert.False(wakeup.IsCompleted);

        await FakeClockDriver.AdvanceAsync(time, DebounceWindow / 2);
        Assert.False(wakeup.IsCompleted);

        await FakeClockDriver.AdvanceUntilCompletedAsync(time, wakeup, DebounceWindow);
    }

    [Fact]
    public async Task SignalsInSeparateQuietWindows_ProduceSeparateWakeups()
    {
        // Arrange
        var firstWakeup = signal.WaitForNextSettledSignalAsync(CancellationToken.None);

        // Act
        signal.Signal();
        await FakeClockDriver.AdvanceUntilCompletedAsync(time, firstWakeup, DebounceWindow);

        var secondWakeup = signal.WaitForNextSettledSignalAsync(CancellationToken.None);
        signal.Signal();

        // Assert: a signal in a later quiet window settles on its own rather than being swallowed by the first.
        await FakeClockDriver.AdvanceUntilCompletedAsync(time, secondWakeup, DebounceWindow);
    }

    [Fact]
    public async Task WaitForNextSettledSignalAsync_WhenCancelledBeforeSignal_Throws()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var wakeup = signal.WaitForNextSettledSignalAsync(cts.Token);

        // Act
        await cts.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wakeup);
    }
}
