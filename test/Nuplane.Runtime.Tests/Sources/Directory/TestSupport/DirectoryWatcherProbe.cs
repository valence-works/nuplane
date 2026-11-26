using System.Diagnostics;

namespace Nuplane.Sources.Directory.Tests.TestSupport;

/// <summary>
/// Synchronizes tests with a live <see cref="FileSystemWatcher" />-backed observation loop.
/// </summary>
public static class DirectoryWatcherProbe
{
    private static readonly byte[] ProbeContent = [0x50, 0x4B];

    /// <summary>
    /// Creates probe packages until the observation loop reports a trigger, then waits for the resulting
    /// signals to drain so they cannot be counted against the scenario under test.
    /// </summary>
    /// <remarks>
    /// Enabling a <see cref="FileSystemWatcher" /> does not guarantee the platform has started delivering
    /// events, and that gap widens under load, so no fixed startup sleep can make the first write observable.
    /// Repeating the stimulus turns watcher readiness into something a test waits for rather than assumes.
    /// </remarks>
    /// <exception cref="TimeoutException">The watcher delivered no events within <paramref name="timeout" />.</exception>
    public static async Task WaitUntilObservingAsync(
        string directoryPath,
        Func<int> triggerCount,
        TimeSpan debounceWindow,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(triggerCount);

        var elapsed = Stopwatch.StartNew();
        for (var probe = 0; triggerCount() == 0; probe++)
        {
            if (elapsed.Elapsed > timeout)
            {
                throw new TimeoutException(
                    $"Directory watcher for '{directoryPath}' delivered no events within {timeout.TotalSeconds:0.#}s.");
            }

            await File.WriteAllBytesAsync(
                Path.Combine(directoryPath, $"probe-{probe}.nupkg"),
                ProbeContent,
                CancellationToken.None);

            await Task.Delay(debounceWindow * 2, CancellationToken.None);
        }

        await Task.Delay(debounceWindow * 4, CancellationToken.None);
    }
}
