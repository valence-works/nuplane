using System.Threading.Channels;

namespace Nuplane.Sources.Directory.Hosting;

/// <summary>
/// Coalesces repeated signals into a single settled notification after a quiet debounce window.
/// </summary>
internal sealed class DebouncedDirtySignal
{
    private readonly TimeSpan _debounceWindow;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest
    });

    /// <param name="debounceWindow">The quiet period that must elapse before a settled notification is produced.</param>
    /// <param name="timeProvider">
    /// The clock used to measure the debounce window. Defaults to <see cref="TimeProvider.System" />; tests supply a
    /// fake clock so debounce behavior can be verified without depending on the wall clock.
    /// </param>
    public DebouncedDirtySignal(TimeSpan debounceWindow, TimeProvider? timeProvider = null)
    {
        if (debounceWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounceWindow), "Debounce window must be zero or greater.");
        }

        _debounceWindow = debounceWindow;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void Signal()
    {
        _signals.Writer.TryWrite(true);
    }

    public async Task WaitForNextSettledSignalAsync(CancellationToken cancellationToken)
    {
        await _signals.Reader.ReadAsync(cancellationToken);
        DrainSignals();

        while (true)
        {
            await Task.Delay(_debounceWindow, _timeProvider, cancellationToken);

            if (!_signals.Reader.TryRead(out _))
            {
                return;
            }

            DrainSignals();
        }
    }

    private void DrainSignals()
    {
        while (_signals.Reader.TryRead(out _))
        {
        }
    }
}