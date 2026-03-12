using Microsoft.Extensions.Logging;
using Nuplane.Abstractions;
using Nuplane.Observability;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Integration.Tests.Reconciliation;

/// <summary>
/// T049 — Regression test verifying that rejected and unavailable admin trigger
/// outcomes are non-mutating and emit explicit outcome codes and failure events.
/// </summary>
public sealed class AdminTriggerFailureRegressionTests
{
    [Fact]
    public async Task Rejected_DoesNotMutateState()
    {
        var ingress = new FakeTriggerIngress(Task.FromResult(new ReconciliationRunResult(true, EmptyChangeSet(), [], false)));
        var captureLogger = new CaptureLogger<ReconciliationLogger>();
        var coordinator = new ManualReconcileCoordinator(ingress, new ReconciliationLogger(captureLogger));

        var outcome = await coordinator.TriggerAsync("corr-1", CancellationToken.None);

        Assert.Equal(ManualReconcileOutcomeCode.Rejected, outcome.OutcomeCode);
        Assert.NotNull(outcome.RunResult);
        Assert.True(outcome.RunResult.Skipped);
        Assert.Empty(outcome.RunResult.FailedPackages);
    }

    [Fact]
    public async Task Rejected_EmitsExplicitOutcomeCode()
    {
        var ingress = new FakeTriggerIngress(Task.FromResult(new ReconciliationRunResult(true, EmptyChangeSet(), [], false)));
        var captureLogger = new CaptureLogger<ReconciliationLogger>();
        var coordinator = new ManualReconcileCoordinator(ingress, new ReconciliationLogger(captureLogger));

        await coordinator.TriggerAsync("corr-2", CancellationToken.None);

        Assert.Contains(captureLogger.Messages, message =>
            message.Contains("OutcomeCode=Rejected", StringComparison.Ordinal)
            && message.Contains("ReasonCode=single-flight-active", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unavailable_DoesNotMutateState()
    {
        var ingress = new ThrowingTriggerIngress(new InvalidOperationException("service down"));
        var captureLogger = new CaptureLogger<ReconciliationLogger>();
        var coordinator = new ManualReconcileCoordinator(ingress, new ReconciliationLogger(captureLogger));

        var outcome = await coordinator.TriggerAsync("corr-3", CancellationToken.None);

        Assert.Equal(ManualReconcileOutcomeCode.Unavailable, outcome.OutcomeCode);
        Assert.Null(outcome.RunResult);
    }

    [Fact]
    public async Task Unavailable_EmitsExplicitOutcomeCode()
    {
        var ingress = new ThrowingTriggerIngress(new InvalidOperationException("service down"));
        var captureLogger = new CaptureLogger<ReconciliationLogger>();
        var coordinator = new ManualReconcileCoordinator(ingress, new ReconciliationLogger(captureLogger));

        await coordinator.TriggerAsync("corr-4", CancellationToken.None);

        Assert.Contains(captureLogger.Messages, message =>
            message.Contains("OutcomeCode=Unavailable", StringComparison.Ordinal)
            && message.Contains("service down", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MultipleRejections_AllNonMutating()
    {
        var ingress = new FakeTriggerIngress(Task.FromResult(new ReconciliationRunResult(true, EmptyChangeSet(), [], false)));
        var captureLogger = new CaptureLogger<ReconciliationLogger>();
        var coordinator = new ManualReconcileCoordinator(ingress, new ReconciliationLogger(captureLogger));

        for (var i = 0; i < 3; i++)
        {
            var outcome = await coordinator.TriggerAsync($"corr-{i}", CancellationToken.None);
            Assert.Equal(ManualReconcileOutcomeCode.Rejected, outcome.OutcomeCode);
        }

        Assert.Equal(3, captureLogger.Messages.Count(message => message.Contains("OutcomeCode=Rejected", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task OperationCanceled_PropagatesWithoutCatch()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var ingress = new ThrowingTriggerIngress(new OperationCanceledException());
        var captureLogger = new CaptureLogger<ReconciliationLogger>();
        var coordinator = new ManualReconcileCoordinator(ingress, new ReconciliationLogger(captureLogger));

        await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.TriggerAsync("corr-5", cts.Token));
        Assert.Empty(captureLogger.Messages);
    }

    private static PackageChangeSet EmptyChangeSet() =>
        new([], [], [], string.Empty, DateTimeOffset.UtcNow);

    private sealed class FakeTriggerIngress(Task<ReconciliationRunResult> resultTask) : IReconciliationTriggerIngress
    {
        public void Enqueue(ReconciliationTrigger trigger)
        {
        }

        public Task<ReconciliationRunResult> EnqueueAndWaitAsync(ReconciliationTrigger trigger, CancellationToken cancellationToken) =>
            resultTask;
    }

    private sealed class ThrowingTriggerIngress(Exception exception) : IReconciliationTriggerIngress
    {
        public void Enqueue(ReconciliationTrigger trigger)
        {
        }

        public Task<ReconciliationRunResult> EnqueueAndWaitAsync(ReconciliationTrigger trigger, CancellationToken cancellationToken) =>
            throw exception;
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
