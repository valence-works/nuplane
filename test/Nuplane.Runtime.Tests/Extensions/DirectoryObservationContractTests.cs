using Microsoft.Extensions.Logging.Abstractions;
using Nuplane.Abstractions;
using Nuplane.DirectorySource;
using Nuplane.DirectorySource.Hosting;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.Models;
using Nuplane.Runtime.Tests.TestSupport;

namespace Nuplane.Runtime.Tests.Extensions;

/// <summary>
/// Contract tests for directory observation coalescing/debounce invariants.
/// Verifies that bursty file-system events are coalesced to at most one
/// reconciliation invocation per debounce window.
/// </summary>
public sealed class DirectoryObservationContractTests
{
    [Fact]
    public async Task BurstyEvents_AreCoalesced_ToAtMostOneTriggerPerDebounceWindow()
    {
        using var tempDir = new TempDirectory();
        var spy = new SpyReconciliationService();
        var options = new DirectorySourceOptions
        {
            FeedName = "test-feed",
            DirectoryPath = tempDir.Path,
            DebounceWindow = TimeSpan.FromMilliseconds(200),
            TriggerReconciliationOnChange = true
        };

        var service = new DirectorySourceReconciliationTriggerHostedService(
            options, spy, NullLogger<DirectorySourceReconciliationTriggerHostedService>.Instance);

        using var cts = new CancellationTokenSource();
        var serviceTask = service.StartAsync(cts.Token);

        // Small delay to let watcher initialize
        await Task.Delay(150);

        // Fire 10 rapid events
        for (var i = 0; i < 10; i++)
        {
            File.WriteAllBytes(Path.Combine(tempDir.Path, $"burst-{i}.nupkg"), [0x50, 0x4B]);
            await Task.Delay(10);
        }

        // Wait for debounce window + processing
        await DebounceAssert.WaitForCountAsync(
            () => spy.TriggerCount,
            1,
            TimeSpan.FromSeconds(5),
            "Expected at least 1 trigger after burst events");

        // Assert coalescing: at most 2 triggers within a generous window
        // (1 for the initial burst + possibly 1 more if trailing events re-trigger)
        await DebounceAssert.AssertCoalescedAsync(
            () => spy.TriggerCount,
            2,
            TimeSpan.FromMilliseconds(500),
            "Bursty events should be coalesced to at most 2 reconciliation invocations");

        cts.Cancel();
        try { await serviceTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task DirectoryChangeTrigger_IncludesFeedNameAsSource()
    {
        using var tempDir = new TempDirectory();
        var spy = new SpyReconciliationService();
        var options = new DirectorySourceOptions
        {
            FeedName = "my-local-feed",
            DirectoryPath = tempDir.Path,
            DebounceWindow = TimeSpan.FromMilliseconds(100),
            TriggerReconciliationOnChange = true
        };

        var service = new DirectorySourceReconciliationTriggerHostedService(
            options, spy, NullLogger<DirectorySourceReconciliationTriggerHostedService>.Instance);

        using var cts = new CancellationTokenSource();
        var serviceTask = service.StartAsync(cts.Token);

        await Task.Delay(150);

        File.WriteAllBytes(Path.Combine(tempDir.Path, "test.nupkg"), [0x50, 0x4B]);

        await DebounceAssert.WaitForCountAsync(
            () => spy.TriggerCount,
            1,
            TimeSpan.FromSeconds(5),
            "Expected trigger after file creation");

        Assert.NotEmpty(spy.Triggers);
        var trigger = spy.Triggers[0];
        Assert.Equal(TriggerType.DirectoryChange, trigger.Type);
        Assert.Equal("my-local-feed", trigger.Source);

        cts.Cancel();
        try { await serviceTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task MultipleDebounceWindows_TriggerSeparateReconciliations()
    {
        using var tempDir = new TempDirectory();
        var spy = new SpyReconciliationService();
        var debounce = TimeSpan.FromMilliseconds(150);
        var options = new DirectorySourceOptions
        {
            FeedName = "multi-window-feed",
            DirectoryPath = tempDir.Path,
            DebounceWindow = debounce,
            TriggerReconciliationOnChange = true
        };

        var service = new DirectorySourceReconciliationTriggerHostedService(
            options, spy, NullLogger<DirectorySourceReconciliationTriggerHostedService>.Instance);

        using var cts = new CancellationTokenSource();
        var serviceTask = service.StartAsync(cts.Token);

        await Task.Delay(150);

        // First event
        File.WriteAllBytes(Path.Combine(tempDir.Path, "first.nupkg"), [0x50, 0x4B]);

        await DebounceAssert.WaitForCountAsync(
            () => spy.TriggerCount,
            1,
            TimeSpan.FromSeconds(5));

        // Wait for full debounce window to pass
        await Task.Delay(debounce + TimeSpan.FromMilliseconds(200));

        // Second event in a new window
        File.WriteAllBytes(Path.Combine(tempDir.Path, "second.nupkg"), [0x50, 0x4B]);

        await DebounceAssert.WaitForCountAsync(
            () => spy.TriggerCount,
            2,
            TimeSpan.FromSeconds(5),
            "Expected 2 triggers in separate debounce windows");

        cts.Cancel();
        try { await serviceTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task NonNupkgFiles_DoNotTriggerReconciliation()
    {
        using var tempDir = new TempDirectory();
        var spy = new SpyReconciliationService();
        var options = new DirectorySourceOptions
        {
            FeedName = "filter-feed",
            DirectoryPath = tempDir.Path,
            DebounceWindow = TimeSpan.FromMilliseconds(100),
            TriggerReconciliationOnChange = true
        };

        var service = new DirectorySourceReconciliationTriggerHostedService(
            options, spy, NullLogger<DirectorySourceReconciliationTriggerHostedService>.Instance);

        using var cts = new CancellationTokenSource();
        var serviceTask = service.StartAsync(cts.Token);

        await Task.Delay(150);

        // Write non-.nupkg files
        File.WriteAllText(Path.Combine(tempDir.Path, "readme.txt"), "hello");
        File.WriteAllText(Path.Combine(tempDir.Path, "data.json"), "{}");

        // Wait for more than a debounce window
        await Task.Delay(TimeSpan.FromMilliseconds(400));

        Assert.Equal(0, spy.TriggerCount);

        cts.Cancel();
        try { await serviceTask; } catch (OperationCanceledException) { }
    }

    private sealed class SpyReconciliationService : IReconciliationService
    {
        private int _triggerCount;
        private readonly List<ReconciliationTrigger> _triggers = [];

        public int TriggerCount => _triggerCount;
        public IReadOnlyList<ReconciliationTrigger> Triggers => _triggers;

        public Task<ReconciliationRunResult> TriggerManualAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _triggerCount);
            return Task.FromResult(SkippedResult());
        }

        public Task<ReconciliationRunResult> TriggerAsync(ReconciliationTrigger trigger, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _triggerCount);
            lock (_triggers)
            {
                _triggers.Add(trigger);
            }
            return Task.FromResult(SkippedResult());
        }

        private static ReconciliationRunResult SkippedResult() =>
            new(true, new PackageChangeSet([], [], [], "test", DateTimeOffset.UtcNow), [], false);
    }
}
