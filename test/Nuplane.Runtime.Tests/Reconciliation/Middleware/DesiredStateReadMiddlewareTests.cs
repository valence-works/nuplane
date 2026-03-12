using Nuplane.Abstractions;
using Nuplane.Observability;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Middleware;
using Nuplane.Reconciliation.Models;
using Nuplane.Sources;
using Nuplane.Store.State;
using Nuplane.Trust;
using Nuplane.Trust.Feeds;
using Nuplane.Trust.Source;

namespace Nuplane.Runtime.Tests.Reconciliation.Middleware;

public sealed class DesiredStateReadMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_SourceReturnsPackages_ContextPopulatedAndNextCalled()
    {
        var nextCalled = false;
        var packages = new[] { Pkg("alpha"), Pkg("beta") };
        var middleware = BuildMiddleware(
            sources: [new FakeSource(packages)],
            aggregatorResult: req => new(req, Empty));

        var ctx = Ctx();
        await middleware.InvokeAsync(ctx, () => { nextCalled = true; return Task.CompletedTask; });

        Assert.True(nextCalled);
        Assert.NotEmpty(ctx.AllowlistedRequests);
        Assert.Contains(ctx.AllowlistedRequests, r => r.Id == "alpha");
        Assert.Contains(ctx.AllowlistedRequests, r => r.Id == "beta");
        Assert.NotNull(ctx.ReadResult);
    }

    [Fact]
    public async Task InvokeAsync_EmptyAggregate_NextStillCalled()
    {
        var nextCalled = false;
        var middleware = BuildMiddleware(
            sources: [new FakeSource([])],
            aggregatorResult: _ => new([], Empty));

        var ctx = Ctx();
        await middleware.InvokeAsync(ctx, () => { nextCalled = true; return Task.CompletedTask; });

        Assert.True(nextCalled);
        Assert.Empty(ctx.AllowlistedRequests);
    }

    [Fact]
    public async Task InvokeAsync_SourceThrows_UsedFallbackTrueAndNextCalled()
    {
        var nextCalled = false;
        var recorder = new FakeFailureRecorder();
        var middleware = BuildMiddleware(
            sources: [new FaultingSource(new InvalidOperationException("feed down"))],
            aggregatorResult: _ => new([], Empty),
            failureRecorder: recorder);

        var ctx = Ctx();
        await middleware.InvokeAsync(ctx, () => { nextCalled = true; return Task.CompletedTask; });

        Assert.True(nextCalled);
        Assert.True(ctx.ReadResult!.UsedFallback);
        Assert.True(recorder.RecordedCount > 0);
    }

    [Fact]
    public async Task InvokeAsync_AggregatorReturnsSourceErrors_ErrorsRecorded()
    {
        var recorder = new FakeFailureRecorder();
        var err = new Exception("test-source-error");
        var middleware = BuildMiddleware(
            sources: [new FakeSource([Pkg("alpha")])],
            aggregatorResult: req => new(req, new Dictionary<string, Exception>
            {
                ["FaultySource"] = err
            }.AsReadOnly()),
            failureRecorder: recorder);

        var ctx = Ctx();
        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.True(recorder.RecordedCount > 0);
    }

    private static DesiredStateReadMiddleware BuildMiddleware(
        IReadOnlyList<IDesiredPackageSource>? sources = null,
        Func<IReadOnlyList<PackageRequest>, DesiredAggregateResult>? aggregatorResult = null,
        IFailureRecorder? failureRecorder = null)
    {
        var fakeStore = new FakeStoreRegistry();
        var snapshotCache = new DesiredSourceSnapshotCache(fakeStore);
        return new(
            sources ?? [],
            new(),
            new FakeAggregator(aggregatorResult ?? (req => new(req, Empty))),
            new PassthroughAllowlistGate(),
            new PassthroughRetryPolicy(),
            snapshotCache,
            failureRecorder ?? new FakeFailureRecorder(),
            new NullLogger(),
            new(new()));
    }

    private static ReconciliationCycleContext Ctx() => new()
    {
        CorrelationId = "test",
        CycleStartedAt = DateTimeOffset.UtcNow,
        CancellationToken = CancellationToken.None
    };

    private static PackageRequest Pkg(string id) =>
        new(id, "1.0.0", "feed-a", PackageUpdatePolicy.Exact, "source-a");

    private static readonly IReadOnlyDictionary<string, Exception> Empty =
        new Dictionary<string, Exception>().AsReadOnly();

    private sealed class FakeSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) =>
            Task.FromResult(requests);
    }

    private sealed class FaultingSource(Exception ex) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) =>
            Task.FromException<IReadOnlyList<PackageRequest>>(ex);
    }

    private sealed class FakeAggregator(Func<IReadOnlyList<PackageRequest>, DesiredAggregateResult> factory) : IDesiredStateAggregator
    {
        public Task<DesiredAggregateResult> AggregateAsync(
            IEnumerable<IDesiredPackageSource> sources,
            SourceTrustOptions trustOptions,
            CancellationToken cancellationToken)
        {
            var requests = sources
                .SelectMany(s => s.GetDesiredAsync(cancellationToken).GetAwaiter().GetResult())
                .ToArray();
            return Task.FromResult(factory(requests));
        }
    }

    private sealed class PassthroughAllowlistGate : IAllowlistGate
    {
        public IReadOnlyList<PackageRequest> Enforce(
            IReadOnlyList<PackageRequest> requests, SourceTrustOptions trustOptions) => requests;

        public void EnsureActiveStorePath(string packageId, string activeInstallPath, string rootDirectory) { }
    }

    private sealed class PassthroughRetryPolicy : IReconciliationRetryPolicy
    {
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
            operation(cancellationToken);
    }

    private sealed class FakeFailureRecorder : IFailureRecorder
    {
        public int RecordedCount { get; private set; }
        public Task RecordAsync(string packageId, string stage, string message, string correlationId, CancellationToken ct)
        {
            RecordedCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class NullLogger : IReconciliationLogger
    {
        public void LogCycleStarted(string correlationId, int requestCount) { }
        public void LogCycleCompleted(string correlationId, bool degraded, int failedCount) { }
        public void LogObserverError(string correlationId, string callbackName, string message) { }
        public void LogFeedDecision(FeedResolutionDecision decision) { }
        public void LogTrustPolicyOutcome(string correlationId, string packageId, FeedTrustPolicyOutcome outcome) { }
        public void LogLockOutcome(string correlationId, string packageId, LockFileEvaluationResult outcome) { }
        public void LogLoadOutcome(string correlationId, string packageId, bool succeeded, string? reason) { }
        public void LogUnloadOutcome(string correlationId, string packageId, string outcome, string? reason) { }
        public void LogManifestOutcome(string correlationId, string sourcePath, string status, string reasonCode, int packageCount) { }
        public void LogSourceOutage(string correlationId, string sourceName, string errorMessage) { }
        public void LogAggregationOutcome(string correlationId, int packageCount, int failedSourceCount) { }
        public void LogLoaderBoundaryOutcome(string correlationId, string packageId, string outcome, string? reasonCode) { }
        public void LogAdminTriggerOutcome(string correlationId, string outcomeCode, string? reasonCode) { }
        public void LogAdminSnapshotRead(string correlationId, int activePackageCount, string healthState) { }
        public void LogTrigger(string correlationId, string triggerType, string? triggerSource) { }
        public void LogIdleModeEntered() { }
        public void LogIdleModeExited() { }
    }

    private sealed class FakeStoreRegistry : IStoreRegistry
    {
        public Task<IReadOnlyDictionary<string, string>> GetActiveVersionsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task<StoreStateRecord> GetStateAsync(CancellationToken ct) =>
            Task.FromResult(StoreStateRecord.Empty());
        public Task PersistActiveVersionsAsync(IReadOnlyDictionary<string, string> active, IReadOnlyDictionary<string, string> applied, string correlationId, CancellationToken ct) =>
            Task.CompletedTask;
        public Task PersistFailureAsync(string packageId, string stage, string message, string correlationId, CancellationToken ct) =>
            Task.CompletedTask;
        public Task PersistSourceSnapshotAsync(string sourceName, SourceSnapshotRef snapshot, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
