using Nuplane.Abstractions;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.Middleware;
using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.Runtime.Tests.Reconciliation.Middleware;

public sealed class TransactionExecutionMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_AllPackagesSucceed_NotifyNotCalled()
    {
        var pkg = Pkg("alpha", "1.0.0");
        var executor = new FakeApplyExecutor(appliedIds: ["alpha"]);
        var dispatcher = new RecordingDispatcher();

        var ctx = Ctx([pkg]);
        await Build(executor, dispatcher).InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Equal(0, dispatcher.NotifyFailedCallCount);
        Assert.NotNull(ctx.ApplyResult);
        Assert.Single(ctx.ApplyResult!.AppliedPackages);
    }

    [Fact]
    public async Task InvokeAsync_OnePackageFails_NotifyCalledForFailedId()
    {
        var packages = new[] { Pkg("alpha", "1.0.0"), Pkg("beta", "2.0.0") };
        var executor = new FakeApplyExecutor(appliedIds: ["alpha"], failedIds: ["beta"]);
        var dispatcher = new RecordingDispatcher();

        var ctx = Ctx(packages);
        await Build(executor, dispatcher).InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Equal(1, dispatcher.NotifyFailedCallCount);
        Assert.Contains("beta", dispatcher.NotifiedPackageIds);
    }

    [Fact]
    public async Task InvokeAsync_AllFail_NotifyCalledForEachFailedId()
    {
        var packages = new[] { Pkg("alpha", "1.0.0"), Pkg("beta", "2.0.0") };
        var executor = new FakeApplyExecutor(appliedIds: [], failedIds: ["alpha", "beta"]);
        var dispatcher = new RecordingDispatcher();

        var ctx = Ctx(packages);
        await Build(executor, dispatcher).InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Equal(2, dispatcher.NotifyFailedCallCount);
    }

    [Fact]
    public async Task InvokeAsync_MergedActiveContainsAppliedVersions()
    {
        var pkg = Pkg("alpha", "1.0.0");
        var executor = new FakeApplyExecutor(appliedIds: ["alpha"]);
        var diffEngine = new FakeDiffEngine(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["alpha"] = "1.0.0" });

        var ctx = Ctx([pkg]);
        ctx.ActiveVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await new TransactionExecutionMiddleware(executor, diffEngine, new NullDispatcher())
            .InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.NotNull(ctx.MergedActive);
        Assert.True(ctx.MergedActive!.ContainsKey("alpha"));
    }

    private static TransactionExecutionMiddleware Build(
        IPackageApplyExecutor executor, IObserverEventDispatcher dispatcher) =>
        new(executor, new FakeDiffEngine(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)), dispatcher);

    private static ReconciliationCycleContext Ctx(ResolvedPackage[] packages)
    {
        var ctx = new ReconciliationCycleContext
        {
            CorrelationId = "test",
            CycleStartedAt = DateTimeOffset.UtcNow,
            CancellationToken = CancellationToken.None
        };
        ctx.ResolutionResult = new(packages, [], []);
        ctx.ActiveVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return ctx;
    }

    private static ResolvedPackage Pkg(string id, string version) =>
        new(id, version, "feed-a", $"/store/{id}", DateTimeOffset.UtcNow, id);

    private sealed class FakeApplyExecutor(
        IReadOnlyList<string>? appliedIds = null,
        IReadOnlyList<string>? failedIds = null) : IPackageApplyExecutor
    {
        public Task<PackageResolutionResult> ResolveAsync(IReadOnlyList<PackageRequest> requests, string correlationId, CancellationToken ct) =>
            Task.FromResult(new PackageResolutionResult([], [], []));

        public Task<PackageApplyExecutionResult> ExecuteTransactionsAsync(PackageResolutionResult result, string correlationId, CancellationToken ct)
        {
            var applied = (appliedIds ?? result.ResolvedPackages.Select(p => p.Id).ToArray())
                .Select(id => result.ResolvedPackages.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    ?? new ResolvedPackage(id, "1.0.0", "feed", $"/store/{id}", DateTimeOffset.UtcNow, id))
                .ToArray();
            return Task.FromResult(new PackageApplyExecutionResult(applied, failedIds ?? []));
        }

        public Task RecordLoadingFailureNonMutatingAsync(string packageId, string correlationId, string message, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FakeDiffEngine(IReadOnlyDictionary<string, string> nextVersions) : IDesiredActualDiffEngine
    {
        public PackageChangeSet Compute(IReadOnlyCollection<ResolvedPackage> desired, IReadOnlyDictionary<string, string> active, string correlationId, DateTimeOffset ts) =>
            new([], [], [], correlationId, ts);

        public IReadOnlyDictionary<string, string> BuildNextActiveVersions(IReadOnlyCollection<ResolvedPackage> desired) =>
            nextVersions;
    }

    private sealed class RecordingDispatcher : IObserverEventDispatcher
    {
        public int NotifyFailedCallCount { get; private set; }
        public List<string> NotifiedPackageIds { get; } = [];

        public Task PublishChangingAsync(PackageChangeSet changeSet, CancellationToken ct) => Task.CompletedTask;
        public Task PublishChangedAsync(PackageChangeSet changeSet, CancellationToken ct) => Task.CompletedTask;
        public Task PublishReconciledAsync(PackageChangeSet changeSet, IReadOnlyList<ResolvedPackage> appliedPackages, CancellationToken ct) => Task.CompletedTask;

        public Task NotifyPackageFailedAsync(string packageId, Exception exception, string correlationId, CancellationToken ct)
        {
            NotifyFailedCallCount++;
            NotifiedPackageIds.Add(packageId);
            return Task.CompletedTask;
        }
    }

    private sealed class NullDispatcher : IObserverEventDispatcher
    {
        public Task PublishChangingAsync(PackageChangeSet changeSet, CancellationToken ct) => Task.CompletedTask;
        public Task PublishChangedAsync(PackageChangeSet changeSet, CancellationToken ct) => Task.CompletedTask;
        public Task PublishReconciledAsync(PackageChangeSet changeSet, IReadOnlyList<ResolvedPackage> appliedPackages, CancellationToken ct) => Task.CompletedTask;
        public Task NotifyPackageFailedAsync(string packageId, Exception exception, string correlationId, CancellationToken ct) => Task.CompletedTask;
    }
}
