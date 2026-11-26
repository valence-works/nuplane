using Nuplane.Abstractions;
using Nuplane.Observability;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Middleware;
using Nuplane.Reconciliation.Models;
using Nuplane.Runtime.Tests.TestSupport;
using Nuplane.Sources;
using Nuplane.Sources.Directory;
using Nuplane.Store.State;

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
        Assert.NotEmpty(ctx.DesiredRequests);
        Assert.Contains(ctx.DesiredRequests, r => r.Id == "alpha");
        Assert.Contains(ctx.DesiredRequests, r => r.Id == "beta");
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
        Assert.Empty(ctx.DesiredRequests);
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

    [Fact]
    public async Task InvokeAsync_TwoFeedRulesAndRestart_FallsBackOnlyForTheUnavailableFeed()
    {
        using var workspace = new TempDirectory();
        var storePath = Path.Combine(workspace.Path, "store-state.json");
        var first = new FeedRuleDesiredSource("first", ["Alpha"]);
        var second = new FeedRuleDesiredSource("second", ["Beta"]);
        await InvokeAsync([first, second], storePath);

        var afterRestart = await InvokeAsync(
            [new FeedRuleDesiredSource("second", ["BetaNext"]), new FaultingAdapter(first)], storePath);

        Assert.Equal(["Alpha", "BetaNext"], afterRestart.DesiredRequests.Select(request => request.Id));
        Assert.False(afterRestart.ReadResult!.AllSourcesFresh);
        Assert.Equal(1, afterRestart.SourceOutageCount);
        var persisted = await OpenStore(storePath).GetStateAsync(CancellationToken.None);
        Assert.Equal(["source-instance:v1:feed-rule:first", "source-instance:v1:feed-rule:second"],
            persisted.LastSuccessfulSourceSnapshots.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task InvokeAsync_TwoDirectoryFeedsUnavailableAfterRestart_KeepsBothSnapshotsAndErrors()
    {
        using var firstFeed = new TempDirectory();
        using var secondFeed = new TempDirectory();
        using var workspace = new TempDirectory();
        var storePath = Path.Combine(workspace.Path, "store-state.json");
        NupkgTestBuilder.Create("Alpha", "1.0.0").BuildTo(firstFeed.Path);
        NupkgTestBuilder.Create("Beta", "1.0.0").BuildTo(secondFeed.Path);
        var first = new DirectoryNupkgDesiredSource("first", firstFeed.Path, ["*"], feedName: "first");
        var second = new DirectoryNupkgDesiredSource("second", secondFeed.Path, ["*"], feedName: "second");
        await InvokeAsync([first, second], storePath);

        var recorder = new FakeFailureRecorder();
        var failingSources = new FaultingAdapter[] { new(second), new(first) };
        var afterRestart = await InvokeAsync(failingSources, storePath, recorder);
        var directAggregate = await new DesiredStateAggregator().AggregateAsync(failingSources, CancellationToken.None);

        Assert.Equal(["Alpha", "Beta"], afterRestart.DesiredRequests.Select(request => request.Id));
        Assert.Equal(["directory:first", "directory:second"], recorder.Identifiers);
        Assert.Equal(recorder.Identifiers, directAggregate.SourceErrors.Keys);
        Assert.Equal(2, afterRestart.SourceOutageCount);
    }

    [Fact]
    public async Task InvokeAsync_LegacyTypeKeyWithKnownOwner_UsesOnlyThatFeedsSnapshotAndRekeysOnFreshRead()
    {
        using var workspace = new TempDirectory();
        var storePath = Path.Combine(workspace.Path, "store-state.json");
        var first = new FeedRuleDesiredSource("first", ["Alpha"]);
        var second = new FeedRuleDesiredSource("second", ["Beta"]);
        var store = OpenStore(storePath);
        await store.PersistSourceSnapshotAsync(
            typeof(FeedRuleDesiredSource).FullName!,
            new("legacy", DateTimeOffset.UtcNow, await first.GetDesiredAsync(CancellationToken.None)),
            CancellationToken.None);

        var restored = await InvokeAsync([new FaultingAdapter(first), new FaultingAdapter(second)], storePath);
        Assert.Equal("Alpha", Assert.Single(restored.DesiredRequests).Id);

        await InvokeAsync([first, second], storePath);
        var persisted = await OpenStore(storePath).GetStateAsync(CancellationToken.None);
        Assert.Contains("source-instance:v1:feed-rule:first", persisted.LastSuccessfulSourceSnapshots.Keys);
        Assert.Contains("source-instance:v1:feed-rule:second", persisted.LastSuccessfulSourceSnapshots.Keys);
    }

    [Fact]
    public async Task InvokeAsync_EmptySuccessfulSnapshotAfterRestart_DoesNotReviveLegacyRequests()
    {
        using var workspace = new TempDirectory();
        var storePath = Path.Combine(workspace.Path, "store-state.json");
        var original = new FeedRuleDesiredSource("first", ["Alpha"]);
        var current = new FeedRuleDesiredSource("first", []);
        await OpenStore(storePath).PersistSourceSnapshotAsync(
            typeof(FeedRuleDesiredSource).FullName!,
            new("legacy", DateTimeOffset.UtcNow, await original.GetDesiredAsync(CancellationToken.None)),
            CancellationToken.None);
        await InvokeAsync([current], storePath);

        var restored = await InvokeAsync([new FaultingAdapter(current)], storePath);

        Assert.True(restored.ReadResult!.UsedFallback);
        Assert.Empty(restored.DesiredRequests);
        var persisted = await OpenStore(storePath).GetStateAsync(CancellationToken.None);
        Assert.Empty(persisted.LastSuccessfulSourceSnapshots["source-instance:v1:feed-rule:first"].Requests!);
    }

    private static async Task<ReconciliationCycleContext> InvokeAsync(
        IReadOnlyList<IDesiredPackageSource> sources, string storePath, IFailureRecorder? recorder = null)
    {
        var context = Ctx();
        var middleware = BuildMiddleware(sources, failureRecorder: recorder, store: OpenStore(storePath));
        await middleware.InvokeAsync(context, () => Task.CompletedTask);
        return context;
    }

    private static StoreRegistry OpenStore(string? path) => new(new StoreStateSerializer(), path);

    private static DesiredStateReadMiddleware BuildMiddleware(
        IReadOnlyList<IDesiredPackageSource>? sources = null,
        Func<IReadOnlyList<PackageRequest>, DesiredAggregateResult>? aggregatorResult = null,
        IFailureRecorder? failureRecorder = null,
        IStoreRegistry? store = null)
    {
        var snapshotCache = new DesiredSourceSnapshotCache(store ?? OpenStore(null));
        return new(
            sources ?? [],
            new FakeAggregator(aggregatorResult ?? (req => new(req, Empty))),
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

    private sealed class FaultingAdapter(IDesiredPackageSource source) : IDesiredPackageSource, IDesiredPackageSourceAdapter
    {
        public IDesiredPackageSource Source => source;

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) =>
            Task.FromException<IReadOnlyList<PackageRequest>>(new IOException("Source unavailable."));
    }

    private sealed class FakeAggregator(Func<IReadOnlyList<PackageRequest>, DesiredAggregateResult> factory) : IDesiredStateAggregator
    {
        public Task<DesiredAggregateResult> AggregateAsync(
            IEnumerable<IDesiredPackageSource> sources,
            CancellationToken cancellationToken)
        {
            var requests = sources
                .SelectMany(s => s.GetDesiredAsync(cancellationToken).GetAwaiter().GetResult())
                .ToArray();
            return Task.FromResult(factory(requests));
        }
    }

    private sealed class PassthroughRetryPolicy : IReconciliationRetryPolicy
    {
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
            operation(cancellationToken);
    }

    private sealed class FakeFailureRecorder : IFailureRecorder
    {
        public List<string> Identifiers { get; } = [];
        public int RecordedCount => Identifiers.Count;

        public Task RecordAsync(string packageId, string stage, string message, string correlationId, CancellationToken ct)
        {
            Identifiers.Add(packageId);
            return Task.CompletedTask;
        }
    }

    private sealed class NullLogger : IReconciliationLogger
    {
        public void LogCycleStarted(string correlationId, int requestCount) { }
        public void LogCycleCompleted(string correlationId, bool degraded, int failedCount) { }
        public void LogObserverError(string correlationId, string callbackName, string message) { }
        public void LogFeedDecision(FeedResolutionDecision decision) { }
        public void LogLockOutcome(string correlationId, string packageId, LockFileEvaluationResult outcome) { }
        public void LogLoadOutcome(string correlationId, string packageId, bool succeeded, string? reason) { }
        public void LogUnloadOutcome(string correlationId, string packageId, string outcome, string? reason) { }
        public void LogManifestOutcome(string correlationId, string sourcePath, string status, string reasonCode, int packageCount) { }
        public void LogSourceOutage(string correlationId, string sourceName, string errorMessage) { }
        public void LogAggregationOutcome(string correlationId, int packageCount, int failedSourceCount) { }
        public void LogLoaderBoundaryOutcome(string correlationId, string packageId, string outcome, string? reasonCode) { }
        public void LogAdminTriggerOutcome(string correlationId, string outcomeCode, string? reasonCode) { }
        public void LogAdminSnapshotRead(string correlationId, int activePackageCount, string healthState) { }
        public void LogOperationalStateContribution(string correlationId, string contributor, int degradedReasonCount) { }
        public void LogTrigger(string correlationId, string triggerType, string? triggerSource) { }
        public void LogIdleModeEntered() { }
        public void LogIdleModeExited() { }
    }
}
