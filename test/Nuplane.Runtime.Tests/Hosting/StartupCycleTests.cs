using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Hosting;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Runtime.Tests.Hosting;

/// <summary>
/// Verifies that automatic reconciliation queues a <see cref="TriggerType.Startup"/> cycle before the first periodic tick.
/// </summary>
public sealed class StartupCycleTests
{
    private static readonly PackageChangeSet EmptyChangeSet =
        new([], [], [], string.Empty, DateTimeOffset.UtcNow);

    [Fact]
    public async Task StartupCycle_FiresBeforePeriodicTick()
    {
        var triggers = new ConcurrentQueue<TriggerType>();
        var service = new TrackingReconciliationService(triggers);
        var (dispatcher, scheduler) = CreateHostedServices(service);

        await dispatcher.StartAsync(CancellationToken.None);
        await scheduler.StartAsync(CancellationToken.None);

        try
        {
            await WaitForConditionAsync(() => triggers.Count >= 1, TimeSpan.FromSeconds(5));

            Assert.NotEmpty(triggers);
            Assert.True(triggers.TryPeek(out var first));
            Assert.Equal(TriggerType.Startup, first);
        }
        finally
        {
            await StopHostedServicesAsync(scheduler, dispatcher);
        }
    }

    [Fact]
    public async Task StartupCycleFailure_IsNonFatal_HostContinuesToPeriodicLoop()
    {
        var service = new FailsFirstReconciliationService();
        var options = new ReconciliationOptions
        {
            EnableAutomaticReconciliation = true,
            PollInterval = TimeSpan.FromMilliseconds(50)
        };

        var (dispatcher, scheduler) = CreateHostedServices(service, options);

        await dispatcher.StartAsync(CancellationToken.None);
        await scheduler.StartAsync(CancellationToken.None);

        try
        {
            await WaitForConditionAsync(() => service.CallCount >= 2, TimeSpan.FromSeconds(5));
            Assert.True(service.CallCount >= 2);
        }
        finally
        {
            await StopHostedServicesAsync(scheduler, dispatcher);
        }
    }

    [Fact]
    public async Task StopAsync_Completes_WhenStartupDispatchObservesCancellation()
    {
        var service = new BlockingCancellationAwareReconciliationService();
        var (dispatcher, scheduler) = CreateHostedServices(service);

        await dispatcher.StartAsync(CancellationToken.None);
        await scheduler.StartAsync(CancellationToken.None);

        await WaitForConditionAsync(() => service.StartedCount >= 1, TimeSpan.FromSeconds(5));
        await StopHostedServicesAsync(scheduler, dispatcher);

        Assert.True(service.CancellationObserved);
    }

    [Fact]
    public void NoStartupCycle_WhenAutomaticReconciliationDisabled()
    {
        var services = new ServiceCollection();
        var stateRoot = Path.Combine(Path.GetTempPath(), "nuplane-ac4-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateRoot);
        try
        {
            var stateFilePath = Path.Combine(stateRoot, "state.json");

            services.AddNuplane(nuplane =>
            {
                nuplane.WithStateFile(stateFilePath);
            });

            var hostedServiceDescriptor = services.FirstOrDefault(d =>
                d.ServiceType == typeof(IHostedService)
                && d.ImplementationType == typeof(ReconciliationHostedService));

            Assert.Null(hostedServiceDescriptor);
        }
        finally
        {
            TryDeleteDirectory(stateRoot);
        }
    }

    #region Helpers

    private static (ReconciliationTriggerDispatcherHostedService Dispatcher, ReconciliationHostedService Scheduler) CreateHostedServices(
        IReconciliationService reconciliationService,
        ReconciliationOptions? options = null)
    {
        options ??= new ReconciliationOptions
        {
            EnableAutomaticReconciliation = true,
            PollInterval = TimeSpan.FromHours(1)
        };

        var queue = new ReconciliationTriggerQueue();
        var metrics = new ReconciliationMetrics(new ReconciliationTelemetry());

        return (
            new ReconciliationTriggerDispatcherHostedService(
                queue,
                reconciliationService,
                metrics,
                NullLogger<ReconciliationTriggerDispatcherHostedService>.Instance),
            new ReconciliationHostedService(
                queue,
                new OptionsWrapper<ReconciliationOptions>(options),
                new OptionsWrapper<ConvergenceOptions>(new ConvergenceOptions()),
                NullLogger<ReconciliationHostedService>.Instance));
    }

    private static async Task StopHostedServicesAsync(ReconciliationHostedService scheduler, ReconciliationTriggerDispatcherHostedService dispatcher)
    {
        await scheduler.StopAsync(CancellationToken.None);
        await dispatcher.StopAsync(CancellationToken.None);
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        if (!condition())
        {
            throw new TimeoutException($"Condition not met within {timeout}.");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class TrackingReconciliationService(ConcurrentQueue<TriggerType> triggers) : IReconciliationService
    {
        public Task<ReconciliationRunResult> TriggerAsync(ReconciliationTrigger trigger, CancellationToken cancellationToken)
        {
            triggers.Enqueue(trigger.Type);
            return Task.FromResult(new ReconciliationRunResult(false, EmptyChangeSet, [], false));
        }
    }

    private sealed class BlockingCancellationAwareReconciliationService : IReconciliationService
    {
        private int _startedCount;
        private int _cancellationObserved;

        public int StartedCount => Volatile.Read(ref _startedCount);
        public bool CancellationObserved => Volatile.Read(ref _cancellationObserved) == 1;

        public async Task<ReconciliationRunResult> TriggerAsync(ReconciliationTrigger trigger, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _startedCount);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _cancellationObserved, 1);
                throw;
            }

            return new ReconciliationRunResult(false, EmptyChangeSet, [], false);
        }
    }

    private sealed class FailsFirstReconciliationService : IReconciliationService
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ReconciliationRunResult> TriggerAsync(ReconciliationTrigger trigger, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _callCount);
            if (count == 1)
            {
                throw new InvalidOperationException("Startup boom");
            }

            return Task.FromResult(new ReconciliationRunResult(false, EmptyChangeSet, [], false));
        }
    }

    #endregion
}
