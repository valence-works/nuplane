using System.Diagnostics;
using Nuplane.Sources.Directory.Hosting;

namespace Nuplane.Sources.Directory.Tests.Hosting;

/// <summary>
/// Contract tests for the debounced dirty-signal primitive used by directory observation.
/// </summary>
public sealed class DebouncedDirtySignalTests
{
    [Fact]
    public async Task BurstSignals_AreCoalesced_IntoASingleSettledWakeup()
    {
        var signal = new DebouncedDirtySignal(TimeSpan.FromMilliseconds(100));

        using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var wakeup = signal.WaitForNextSettledSignalAsync(waitCts.Token);

        for (var i = 0; i < 10; i++)
        {
            signal.Signal();
            await Task.Delay(10);
        }

        await wakeup;

        using var probeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(75));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await signal.WaitForNextSettledSignalAsync(probeCts.Token));
    }

    [Fact]
    public async Task SignalDuringDebounce_ExtendsTheQuietWindow()
    {
        var signal = new DebouncedDirtySignal(TimeSpan.FromMilliseconds(150));
        var stopwatch = Stopwatch.StartNew();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var wakeup = signal.WaitForNextSettledSignalAsync(cts.Token);

        signal.Signal();
        await Task.Delay(100, CancellationToken.None);
        signal.Signal();

        await wakeup;
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(220),
            $"Expected trailing-edge debounce to defer wakeup until after the second signal. Actual: {stopwatch.Elapsed.TotalMilliseconds}ms.");
    }

    [Fact]
    public async Task SignalsInSeparateQuietWindows_ProduceSeparateWakeups()
    {
        var signal = new DebouncedDirtySignal(TimeSpan.FromMilliseconds(80));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var firstWakeup = signal.WaitForNextSettledSignalAsync(cts.Token);
        signal.Signal();
        await firstWakeup;

        await Task.Delay(120, CancellationToken.None);

        var secondWakeup = signal.WaitForNextSettledSignalAsync(cts.Token);
        signal.Signal();
        await secondWakeup;
    }

    [Fact]
    public async Task WaitForNextSettledSignalAsync_WhenCancelledBeforeSignal_Throws()
    {
        var signal = new DebouncedDirtySignal(TimeSpan.FromMilliseconds(100));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await signal.WaitForNextSettledSignalAsync(cts.Token));
    }
}