using Microsoft.Extensions.Logging.Abstractions;
using Nuplane.Health;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;
using Nuplane.Sources.Directory.Hosting;
using Nuplane.Sources.Directory.Tests.TestSupport;

namespace Nuplane.Sources.Directory.Tests.Hosting;

/// <summary>
/// Contract tests for directory observation coalescing/debounce invariants.
/// </summary>
/// <remarks>
/// These tests drive a real <see cref="FileSystemWatcher" />, so they cannot use a fake clock. The tests that
/// wait for a trigger stay load-tolerant instead: readiness is awaited via <see cref="DirectoryWatcherProbe" />
/// rather than assumed after a fixed sleep, and every wait ceiling is generous relative to the debounce window.
/// </remarks>
public sealed class DirectoryObservationContractTests : IAsyncDisposable
{
    private static readonly byte[] NupkgContent = [0x50, 0x4B];
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Kept short so that a trigger which should never arrive would still have time to show up within the
    /// fixed observation window used by the negative test below.
    /// </summary>
    private static readonly TimeSpan NonNupkgDebounceWindow = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// A ceiling, not an expectation: the happy path returns as soon as the trigger lands, so being generous
    /// costs nothing and stops a loaded machine from being mistaken for a broken watcher.
    /// </summary>
    private static readonly TimeSpan WatcherTimeout = TimeSpan.FromSeconds(30);

    private readonly TempDirectory tempDir = new();
    private readonly SpyTriggerSink spy = new();
    private DirectorySourceReconciliationTriggerHostedService? service;

    [Fact]
    public async Task BurstyEvents_AreCoalesced_ToAtMostOneTriggerPerDebounceWindow()
    {
        // Arrange
        await StartObservingAsync("test-feed");

        // Act
        for (var i = 0; i < 10; i++)
        {
            await WriteNupkgAsync($"burst-{i}.nupkg");
        }

        // Assert
        await DebounceAssert.WaitForCountAsync(
            () => spy.TriggerCount,
            1,
            WatcherTimeout,
            "Expected at least 1 queued trigger after burst events");

        await DebounceAssert.AssertCoalescedAsync(
            () => spy.TriggerCount,
            2,
            DebounceWindow * 4,
            "Bursty events should be coalesced to at most 2 queued triggers");
    }

    [Fact]
    public async Task ObservedChangeTrigger_IncludesStructuredFeedOrigin()
    {
        // Arrange
        await StartObservingAsync("my-local-feed");

        // Act
        await WriteNupkgAsync("test.nupkg");

        await DebounceAssert.WaitForCountAsync(
            () => spy.TriggerCount,
            1,
            WatcherTimeout,
            "Expected trigger after file creation");

        // Assert
        var trigger = Assert.Single(spy.Triggers);
        Assert.Equal(TriggerType.ObservedChange, trigger.Type);
        Assert.NotNull(trigger.ObservedOrigin);
        Assert.Equal("my-local-feed", trigger.ObservedOrigin.FeedName);
        Assert.Equal(FeedObservationKind.DirectoryWatcher, trigger.ObservedOrigin.Kind);
    }

    [Fact]
    public async Task NonNupkgFiles_DoNotTriggerReconciliation()
    {
        // Arrange
        await StartAsync("filter-feed", NonNupkgDebounceWindow);
        await Task.Delay(150, CancellationToken.None);

        // Act
        await File.WriteAllTextAsync(Path.Combine(tempDir.Path, "readme.txt"), "hello", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(tempDir.Path, "data.json"), "{}", CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(400), CancellationToken.None);

        // Assert
        Assert.Equal(0, spy.TriggerCount);
    }

    /// <summary>
    /// Starts the observation loop, waits until the watcher is demonstrably delivering events, and clears the
    /// probe triggers so the caller starts from a known-empty spy.
    /// </summary>
    private async Task StartObservingAsync(string feedName)
    {
        await StartAsync(feedName, DebounceWindow);

        await DirectoryWatcherProbe.WaitUntilObservingAsync(
            tempDir.Path,
            () => spy.TriggerCount,
            DebounceWindow,
            WatcherTimeout);

        spy.Reset();
    }

    private async Task StartAsync(string feedName, TimeSpan debounceWindow)
    {
        var options = new DirectorySourceOptions
        {
            FeedName = feedName,
            DirectoryPath = tempDir.Path,
            DebounceWindow = debounceWindow,
            TriggerReconciliationOnChange = true
        };

        service = new DirectorySourceReconciliationTriggerHostedService(
            options,
            spy,
            NullLogger<DirectorySourceReconciliationTriggerHostedService>.Instance,
            new ObservationDegradationTracker());

        await service.StartAsync(CancellationToken.None);
    }

    private Task WriteNupkgAsync(string fileName) =>
        File.WriteAllBytesAsync(Path.Combine(tempDir.Path, fileName), NupkgContent, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        if (service is not null)
        {
            try
            {
                await service.StopAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
            }

            service.Dispose();
        }

        tempDir.Dispose();
    }

    private sealed class SpyTriggerSink : IReconciliationTriggerIngress
    {
        private readonly List<ReconciliationTrigger> triggers = [];

        public int TriggerCount
        {
            get
            {
                lock (triggers)
                {
                    return triggers.Count;
                }
            }
        }

        public IReadOnlyList<ReconciliationTrigger> Triggers
        {
            get
            {
                lock (triggers)
                {
                    return triggers.ToArray();
                }
            }
        }

        public void Reset()
        {
            lock (triggers)
            {
                triggers.Clear();
            }
        }

        public void Enqueue(ReconciliationTrigger trigger)
        {
            lock (triggers)
            {
                triggers.Add(trigger);
            }
        }

        public Task<ReconciliationRunResult> EnqueueAndWaitAsync(ReconciliationTrigger trigger, CancellationToken cancellationToken)
        {
            Enqueue(trigger);
            var changeSet = new Nuplane.Abstractions.PackageChangeSet([], [], [], "test", DateTimeOffset.UtcNow);
            return Task.FromResult(new ReconciliationRunResult(false, changeSet, [], false));
        }
    }
}
