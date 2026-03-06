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
/// trigger emission per debounce window.
/// </summary>
public sealed class DirectoryObservationContractTests
{
    [Fact]
    public async Task BurstyEvents_AreCoalesced_ToAtMostOneTriggerPerDebounceWindow()
    {
        using var tempDir = new TempDirectory();
        var spy = new SpyTriggerSink();
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

        await Task.Delay(150);

        for (var i = 0; i < 10; i++)
        {
            File.WriteAllBytes(Path.Combine(tempDir.Path, $"burst-{i}.nupkg"), [0x50, 0x4B]);
            await Task.Delay(10);
        }

        await DebounceAssert.WaitForCountAsync(
            () => spy.TriggerCount,
            1,
            TimeSpan.FromSeconds(5),
            "Expected at least 1 queued trigger after burst events");

        await DebounceAssert.AssertCoalescedAsync(
            () => spy.TriggerCount,
            2,
            TimeSpan.FromMilliseconds(500),
            "Bursty events should be coalesced to at most 2 queued triggers");

        cts.Cancel();
        try { await serviceTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ObservedChangeTrigger_IncludesFeedNameAsSource()
    {
        using var tempDir = new TempDirectory();
        var spy = new SpyTriggerSink();
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
        Assert.Equal(TriggerType.ObservedChange, trigger.Type);
        Assert.Equal("my-local-feed", trigger.Source);
        Assert.Equal(FeedObservationKind.DirectoryWatcher, trigger.ObservationKind);

        cts.Cancel();
        try { await serviceTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task MultipleDebounceWindows_TriggerSeparateReconciliations()
    {
        using var tempDir = new TempDirectory();
        var spy = new SpyTriggerSink();
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

        File.WriteAllBytes(Path.Combine(tempDir.Path, "first.nupkg"), [0x50, 0x4B]);

        await DebounceAssert.WaitForCountAsync(
            () => spy.TriggerCount,
            1,
            TimeSpan.FromSeconds(5));

        await Task.Delay(debounce + TimeSpan.FromMilliseconds(200));

        File.WriteAllBytes(Path.Combine(tempDir.Path, "second.nupkg"), [0x50, 0x4B]);

        await DebounceAssert.WaitForCountAsync(
            () => spy.TriggerCount,
            2,
            TimeSpan.FromSeconds(5),
            "Expected 2 queued triggers in separate debounce windows");

        cts.Cancel();
        try { await serviceTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task NonNupkgFiles_DoNotTriggerReconciliation()
    {
        using var tempDir = new TempDirectory();
        var spy = new SpyTriggerSink();
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

        File.WriteAllText(Path.Combine(tempDir.Path, "readme.txt"), "hello");
        File.WriteAllText(Path.Combine(tempDir.Path, "data.json"), "{}");

        await Task.Delay(TimeSpan.FromMilliseconds(400));

        Assert.Equal(0, spy.TriggerCount);

        cts.Cancel();
        try { await serviceTask; } catch (OperationCanceledException) { }
    }

    private sealed class SpyTriggerSink : IReconciliationTriggerIngress
    {
        private int _triggerCount;
        private readonly List<ReconciliationTrigger> _triggers = [];

        public int TriggerCount => _triggerCount;
        public IReadOnlyList<ReconciliationTrigger> Triggers
        {
            get
            {
                lock (_triggers)
                {
                    return _triggers.ToArray();
                }
            }
        }

        public void Enqueue(ReconciliationTrigger trigger)
        {
            Interlocked.Increment(ref _triggerCount);
            lock (_triggers)
            {
                _triggers.Add(trigger);
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
