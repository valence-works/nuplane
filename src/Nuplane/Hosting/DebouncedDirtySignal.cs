using System.Threading.Channels;

namespace Nuplane.Hosting;

/// <summary>
/// Coalesces repeated signals into a single settled notification after a quiet debounce window.
/// </summary>
internal sealed class DebouncedDirtySignal
{
    private readonly TimeSpan _debounceWindow;
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest
    });

    public DebouncedDirtySignal(TimeSpan debounceWindow)
    {
        if (debounceWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounceWindow), "Debounce window must be zero or greater.");
        }

        _debounceWindow = debounceWindow;
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
            await Task.Delay(_debounceWindow, cancellationToken);

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
