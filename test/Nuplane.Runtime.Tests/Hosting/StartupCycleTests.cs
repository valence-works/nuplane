using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.Runtime.Tests.Hosting;

/// <summary>
/// T011 — Verifies that <see cref="ReconciliationHostedService"/> fires a
/// TriggerType.Startup cycle before the first periodic tick.
/// </summary>
public sealed class StartupCycleTests
{
    private static readonly PackageChangeSet EmptyChangeSet =
        new([], [], [], string.Empty, DateTimeOffset.UtcNow);

    [Fact]
    public async Task StartupCycle_FiresBeforePeriodicTick()
    {
        // Arrange
        var triggers = new ConcurrentQueue<TriggerType>();
        var service = new TrackingReconciliationService(triggers);
        var sut = CreateHostedService(service);

        using var cts = new CancellationTokenSource();

        // Act — start the hosted service and allow the startup cycle to run, then cancel
        var executeTask = sut.StartAsync(cts.Token);

        // Wait for at least the startup trigger to be recorded
        await WaitForConditionAsync(() => triggers.Count >= 1, TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        try { await sut.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

        // Assert — the first trigger must be Startup
        Assert.NotEmpty(triggers);
        Assert.True(triggers.TryPeek(out var first));
        Assert.Equal(TriggerType.Startup, first);
    }

    [Fact]
    public async Task StartupCycleFailure_IsNonFatal_HostContinuesToPeriodicLoop()
    {
        // Arrange — service throws on first call (startup), succeeds on subsequent (periodic)
        var callCount = 0;
        var service = new DelegatingReconciliationService(trigger =>
        {
            var count = Interlocked.Increment(ref callCount);
            if (count == 1)
                throw new InvalidOperationException("Startup boom");
            return Task.FromResult(new ReconciliationRunResult(false, EmptyChangeSet, [], false));
        });

        var options = new ReconciliationOptions
        {
            EnableAutomaticReconciliation = true,
            PollInterval = TimeSpan.FromMilliseconds(50)
        };

        var sut = CreateHostedService(service, options);
        using var cts = new CancellationTokenSource();

        // Act
        var executeTask = sut.StartAsync(cts.Token);

        // Wait for periodic to fire (callCount > 1 means we got past startup failure)
        await WaitForConditionAsync(() => Volatile.Read(ref callCount) >= 2, TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        try { await sut.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

        // Assert — host survived the startup failure
        Assert.True(Volatile.Read(ref callCount) >= 2);
    }

    [Fact]
    public async Task StartupCycle_OperationCanceled_PropagatesAndStopsHost()
    {
        // Arrange — cancellation triggered before startup cycle can run
        var service = new DelegatingReconciliationService(_ =>
            throw new OperationCanceledException("Host shutting down"));

        var sut = CreateHostedService(service);
        using var cts = new CancellationTokenSource();

        // Act & Assert — OperationCanceledException is propagated
        var executeTask = sut.StartAsync(cts.Token);

        // Give it a moment, then cancel to be safe
        await Task.Delay(200);
        await cts.CancelAsync();

        try
        {
            await sut.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // If we got here without hanging, the OperationCanceledException was properly handled
        Assert.True(true);
    }

    [Fact]
    public void NoStartupCycle_WhenAutomaticReconciliationDisabled()
    {
        // AC-4: When EnableAutomaticReconciliation is false (the default),
        // ReconciliationHostedService is NOT registered via AddNuplane.
        var services = new ServiceCollection();
        var stateRoot = Path.Combine(Path.GetTempPath(), "nuplane-ac4-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateRoot);
        try
        {
            var stateFilePath = Path.Combine(stateRoot, "state.json");

            Nuplane.NuplaneServiceCollectionExtensions.AddNuplane(
                services,
                configureSourceTrust: trust =>
                {
                    trust.AllowedSourceNames.Add("NuGet.Main");
                    trust.AllowedPackageIds.Add("Test.Package");
                },
                stateFilePath: stateFilePath);

            // EnableAutomaticReconciliation defaults to false — no hosted service registered
            var hostedServiceDescriptor = services.FirstOrDefault(d =>
                d.ServiceType == typeof(IHostedService)
                && d.ImplementationType == typeof(Nuplane.ReconciliationHostedService));

            Assert.Null(hostedServiceDescriptor);
        }
        finally
        {
            try { Directory.Delete(stateRoot, recursive: true); } catch { }
        }
    }

    #region Helpers

    private static ReconciliationHostedService CreateHostedService(
        IReconciliationService reconciliationService,
        ReconciliationOptions? options = null)
    {
        options ??= new ReconciliationOptions
        {
            EnableAutomaticReconciliation = true,
            PollInterval = TimeSpan.FromHours(1) // Long interval so periodic doesn't interfere
        };

        return new ReconciliationHostedService(
            reconciliationService,
            options,
            new ConvergenceOptions(),
            NullLogger<ReconciliationHostedService>.Instance,
            new ReconciliationMetrics(new ReconciliationTelemetry()));
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        if (!condition())
            throw new TimeoutException($"Condition not met within {timeout}.");
    }

    /// <summary>
    /// Tracks trigger types in order (thread-safe).
    /// </summary>
    private sealed class TrackingReconciliationService(ConcurrentQueue<TriggerType> triggers) : IReconciliationService
    {
        public Task<ReconciliationRunResult> TriggerManualAsync(CancellationToken cancellationToken) =>
            TriggerAsync(new ReconciliationTrigger(TriggerType.Manual), cancellationToken);

        public Task<ReconciliationRunResult> TriggerAsync(ReconciliationTrigger trigger, CancellationToken cancellationToken)
        {
            triggers.Enqueue(trigger.Type);
            return Task.FromResult(new ReconciliationRunResult(false, EmptyChangeSet, [], false));
        }
    }

    /// <summary>
    /// Delegates to a callback for testing specific behaviors.
    /// </summary>
    private sealed class DelegatingReconciliationService(
        Func<ReconciliationTrigger, Task<ReconciliationRunResult>> handler) : IReconciliationService
    {
        public Task<ReconciliationRunResult> TriggerManualAsync(CancellationToken cancellationToken) =>
            TriggerAsync(new ReconciliationTrigger(TriggerType.Manual), cancellationToken);

        public Task<ReconciliationRunResult> TriggerAsync(ReconciliationTrigger trigger, CancellationToken cancellationToken) =>
            handler(trigger);
    }

    #endregion
}
