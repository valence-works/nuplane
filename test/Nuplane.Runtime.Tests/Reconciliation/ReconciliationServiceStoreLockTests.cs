using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Nuplane.Abstractions;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Cleanup;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Reconciliation;

/// <summary>
/// The invariant these pin: while a reconciliation cycle runs against a persisted store, no second
/// cycle — in this process or any other — runs against the same state file, and the lock that
/// enforces it is released on every way out of the cycle.
/// </summary>
public sealed class ReconciliationServiceStoreLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nuplane-recon-store-lock", Guid.NewGuid().ToString("N"));
    private readonly string _stateFilePath;
    private readonly CountingSource _source = new();

    public ReconciliationServiceStoreLockTests()
    {
        Directory.CreateDirectory(_root);
        _stateFilePath = Path.Combine(_root, "store-state.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task TriggerAsync_WhenAnotherProcessOwnsTheStore_SkipsWithStoreLockUnavailable()
    {
        using var heldElsewhere = CreateStoreLock().Acquire();
        var service = CreateService();

        var result = await service.TriggerManualAsync(CancellationToken.None);

        Assert.True(result.Skipped);
        Assert.Equal(ReconciliationSkipReason.StoreLockUnavailable, result.SkipReason);
        Assert.False(result.IsDegraded);
    }

    [Fact]
    public async Task TriggerAsync_WhenAnotherProcessOwnsTheStore_RunsNoneOfThePipeline()
    {
        using var heldElsewhere = CreateStoreLock().Acquire();
        var service = CreateService();

        await service.TriggerManualAsync(CancellationToken.None);

        Assert.Equal(0, _source.ReadCount);
        Assert.False(File.Exists(_stateFilePath));
    }

    [Fact]
    public async Task TriggerAsync_WhenStoreLockIsAvailable_RunsAndReportsNoSkipReason()
    {
        var service = CreateService();

        var result = await service.TriggerManualAsync(CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.Equal(ReconciliationSkipReason.None, result.SkipReason);
        Assert.Equal(1, _source.ReadCount);
    }

    [Fact]
    public async Task TriggerAsync_AfterACycleCompletes_ReleasesTheStoreLock()
    {
        var service = CreateService();
        await service.TriggerManualAsync(CancellationToken.None);

        using var afterwards = CreateStoreLock().Acquire();

        Assert.Equal(StoreLockOutcome.Acquired, afterwards.Outcome);
    }

    [Fact]
    public async Task TriggerAsync_WhenThePipelineThrows_ReleasesTheStoreLock()
    {
        var cleanup = Substitute.For<IPackageCleanupService>();
        cleanup
            .ExecuteAutomaticAsync(default!, default!, default!, default, default)
            .ReturnsForAnyArgs<Task<IReadOnlyList<CleanupDecision>>>(_ => throw new InvalidOperationException("cleanup exploded"));
        var service = CreateService(packageCleanupService: cleanup);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TriggerManualAsync(CancellationToken.None));

        using var afterwards = CreateStoreLock().Acquire();
        Assert.Equal(StoreLockOutcome.Acquired, afterwards.Outcome);
    }

    [Fact]
    public async Task TriggerAsync_WhenTheCycleIsCancelled_ReleasesTheStoreLock()
    {
        using var cancellation = new CancellationTokenSource();
        var service = CreateService(sources: [new CancellingSource(cancellation)]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.TriggerManualAsync(cancellation.Token));

        using var afterwards = CreateStoreLock().Acquire();
        Assert.Equal(StoreLockOutcome.Acquired, afterwards.Outcome);
    }

    [Fact]
    public async Task TriggerAsync_WhenStoreLockIsDisabled_RunsEvenWhileAnotherHolderOwnsTheStore()
    {
        using var heldElsewhere = CreateStoreLock().Acquire();
        var service = CreateService(storeLock: CreateStoreLock(enableStoreLock: false));

        var result = await service.TriggerManualAsync(CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.Equal(1, _source.ReadCount);
    }

    [Fact]
    public async Task TriggerAsync_WhenStoreIsInMemory_RunsAndLocksNothing()
    {
        var inMemoryLock = new StoreLock(
            EffectiveStorePersistenceSettings.Resolve(new() { UseInMemoryStore = true }),
            new OptionsWrapper<ReconciliationOptions>(new()),
            NullLogger<StoreLock>.Instance);
        var service = CreateService(storeLock: inMemoryLock, storeRegistry: new StoreRegistry(new StoreStateSerializer(), stateFilePath: null));

        var result = await service.TriggerManualAsync(CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.Empty(Directory.EnumerateFiles(_root));
    }

    [Fact]
    public async Task TriggerAsync_WhenSingleFlightDeclinesACycle_ReportsSingleFlightRatherThanStoreContention()
    {
        var service = CreateService(sources: [new ReentrantSource(_source)]);

        var result = await service.TriggerManualAsync(CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.Equal(ReconciliationSkipReason.SingleFlight, ReentrantSource.LastNestedResult!.SkipReason);
        Assert.True(ReentrantSource.LastNestedResult.Skipped);
    }

    private ReconciliationService CreateService(
        IEnumerable<IDesiredPackageSource>? sources = null,
        IPackageCleanupService? packageCleanupService = null,
        IStoreLock? storeLock = null,
        IStoreRegistry? storeRegistry = null)
    {
        var service = ReconciliationServiceFactory.Create(
            sources: sources ?? [_source],
            storeRegistry: storeRegistry ?? new StoreRegistry(new StoreStateSerializer(), _stateFilePath),
            reconciliationOptions: new() { MaxRetryAttempts = 0 },
            packageCleanupService: packageCleanupService,
            storeLock: storeLock ?? CreateStoreLock());

        ReentrantSource.Host = service;
        return service;
    }

    private StoreLock CreateStoreLock(bool enableStoreLock = true) =>
        new(
            EffectiveStorePersistenceSettings.Resolve(new() { StateFilePath = _stateFilePath }),
            new OptionsWrapper<ReconciliationOptions>(new() { EnableStoreLock = enableStoreLock }),
            NullLogger<StoreLock>.Instance);

    private sealed class CountingSource : IDesiredPackageSource
    {
        public int ReadCount { get; private set; }

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
        {
            ReadCount++;
            return Task.FromResult<IReadOnlyList<PackageRequest>>([]);
        }
    }

    private sealed class CancellingSource(CancellationTokenSource cancellation) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
        {
            cancellation.Cancel();
            return Task.FromResult<IReadOnlyList<PackageRequest>>([]);
        }
    }

    /// <summary>
    /// Re-enters the cycle from inside it, which single-flight declines before the store lock is
    /// ever consulted — so the two reasons stay distinguishable.
    /// </summary>
    private sealed class ReentrantSource(IDesiredPackageSource inner) : IDesiredPackageSource
    {
        public static ReconciliationService? Host { get; set; }

        public static ReconciliationRunResult? LastNestedResult { get; private set; }

        public async Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
        {
            LastNestedResult = await Host!.TriggerManualAsync(ct);
            return await inner.GetDesiredAsync(ct);
        }
    }
}
