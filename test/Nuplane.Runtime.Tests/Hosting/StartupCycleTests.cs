using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Hosting;
using Nuplane.Observability;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Reconciliation.Convergence;
using Nuplane.Reconciliation.Models;
using Nuplane.Runtime.Tests.TestSupport;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Hosting;

/// <summary>
/// Verifies that automatic reconciliation queues a <see cref="TriggerType.Startup"/> cycle before the first periodic tick.
/// </summary>
public sealed class StartupCycleTests
{
    private static readonly PackageChangeSet EmptyChangeSet =
        new([], [], [], string.Empty, DateTimeOffset.UtcNow);

    [Fact]
    public void PollEvery_RegistersAutomaticReconciliationOptionsAndHostedServices()
    {
        var services = new ServiceCollection();

        services.AddNuplane(nuplane =>
        {
            nuplane.PollEvery(TimeSpan.FromSeconds(15));
        });

        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ReconciliationOptions>>().Value;
        var hostedServiceTypes = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType)
            .Where(static type => type is not null)
            .ToArray();

        Assert.True(options.EnableAutomaticReconciliation);
        Assert.Equal(TimeSpan.FromSeconds(15), options.PollInterval);
        Assert.Contains(typeof(ReconciliationHostedService), hostedServiceTypes);
        Assert.Contains(typeof(ReconciliationTriggerDispatcherHostedService), hostedServiceTypes);
    }

    [Fact]
    public async Task StartupCycle_FiresBeforePeriodicTick()
    {
        var service = new TrackingReconciliationService();
        var (dispatcher, scheduler, startup) = CreateHostedServices(service);

        await dispatcher.StartAsync(CancellationToken.None);
        await startup.StartAsync(CancellationToken.None);
        await scheduler.StartAsync(CancellationToken.None);

        try
        {
            var first = await service.WaitForFirstTriggerAsync().WaitAsync(TimeSpan.FromSeconds(5));
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

        var (dispatcher, scheduler, _) = CreateHostedServices(service, options);

        await dispatcher.StartAsync(CancellationToken.None);
        await scheduler.StartAsync(CancellationToken.None);

        try
        {
            await service.WaitForSecondCallAsync().WaitAsync(TimeSpan.FromSeconds(5));
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
        var (dispatcher, scheduler, startup) = CreateHostedServices(service);

        await dispatcher.StartAsync(CancellationToken.None);

        // Start NuplaneStartupHostedService on a background thread — it blocks until the
        // startup reconciliation completes (or is cancelled), so it cannot be awaited inline.
        using var startupCts = new CancellationTokenSource();
        var startupTask = startup.StartAsync(startupCts.Token);

        await service.WaitUntilStartedAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // Cancel the startup service and stop the dispatcher so the blocking TriggerAsync observes cancellation.
        startupCts.Cancel();
        await StopHostedServicesAsync(scheduler, dispatcher);

        await service.WaitForCancellationObservedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.CancellationObserved);
    }

    [Fact]
    public async Task StartupCycleFailurePolicyFailHost_WhenStartupCompletesDegraded_ThrowsStartupException()
    {
        var service = new DegradedStartupReconciliationService();
        var (dispatcher, scheduler, startup) = CreateHostedServices(service);

        await dispatcher.StartAsync(CancellationToken.None);

        try
        {
            var exception = await Assert.ThrowsAsync<NuplaneStartupReconciliationException>(
                () => startup.StartAsync(CancellationToken.None));

            Assert.Contains("pkg-failed", exception.FailedPackageIds);
            Assert.False(string.IsNullOrWhiteSpace(exception.CorrelationId));
            Assert.NotNull(exception.RunResult);
        }
        finally
        {
            await StopHostedServicesAsync(scheduler, dispatcher);
        }
    }

    [Fact]
    public async Task StartupCycleFailurePolicyStartDegraded_WhenStartupCompletesDegraded_DoesNotThrow()
    {
        var service = new DegradedStartupReconciliationService();
        var options = new ReconciliationOptions
        {
            StartupFailurePolicy = StartupFailurePolicy.StartDegraded
        };
        var (dispatcher, scheduler, startup) = CreateHostedServices(service, options);

        await dispatcher.StartAsync(CancellationToken.None);

        try
        {
            await startup.StartAsync(CancellationToken.None);
        }
        finally
        {
            await StopHostedServicesAsync(scheduler, dispatcher);
        }
    }

    [Fact]
    public async Task StartupCycleFailurePolicyUseLastKnownGood_WhenStartupCompletesDegraded_Throws()
    {
        var service = new DegradedStartupReconciliationService();
        var options = new ReconciliationOptions
        {
            StartupFailurePolicy = StartupFailurePolicy.UseLastKnownGood
        };
        var (dispatcher, scheduler, startup) = CreateHostedServices(service, options);

        await dispatcher.StartAsync(CancellationToken.None);

        try
        {
            var exception = await Assert.ThrowsAsync<NuplaneStartupReconciliationException>(
                () => startup.StartAsync(CancellationToken.None));

            Assert.Contains("pkg-failed", exception.FailedPackageIds);
        }
        finally
        {
            await StopHostedServicesAsync(scheduler, dispatcher);
        }
    }

    [Fact]
    public async Task StartupCycleFailurePolicyUseLastKnownGood_WhenValidActiveLkgExists_PublishesRecoveredPackages()
    {
        var service = new DegradedStartupReconciliationService();
        var options = new ReconciliationOptions
        {
            StartupFailurePolicy = StartupFailurePolicy.UseLastKnownGood
        };
        using var installRoot = new TempDirectory();
        var (store, packageInstallPath) = await CreateValidLastKnownGoodStoreAsync(installRoot);
        var dispatcher = new RecordingObserverDispatcher();
        var startupRecoveryState = new StartupRecoveryState();
        var recovery = new LastKnownGoodStartupRecoveryService(store, dispatcher, startupRecoveryState);
        var (queueDispatcher, scheduler, startup) = CreateHostedServices(service, options, recovery);

        await queueDispatcher.StartAsync(CancellationToken.None);

        try
        {
            await startup.StartAsync(CancellationToken.None);

            var recovered = Assert.Single(dispatcher.ReconciledPackages);
            Assert.Equal("pkg-a", recovered.Id);
            Assert.Equal(packageInstallPath, recovered.InstallPath);

            var contribution = startupRecoveryState.GetContribution();
            Assert.Contains("startup-lkg-recovery-active:1", contribution.DegradedReasons);
        }
        finally
        {
            await StopHostedServicesAsync(scheduler, queueDispatcher);
        }
    }

    [Fact]
    public async Task LastKnownGoodRecovery_WhenRecoveredLoadFails_ReturnsFailure()
    {
        using var installRoot = new TempDirectory();
        var (store, _) = await CreateValidLastKnownGoodStoreAsync(installRoot);
        var loadFailures = new RecordingCycleFailureContributor();
        var dispatcher = new RecordingObserverDispatcher((changeSet, packages) =>
        {
            foreach (var package in packages)
            {
                loadFailures.RecordFailure(changeSet.CorrelationId, package.Id);
            }
        });
        var startupRecoveryState = new StartupRecoveryState();
        var recovery = new LastKnownGoodStartupRecoveryService(store, dispatcher, startupRecoveryState, [loadFailures]);

        var result = await recovery.TryRecoverAsync("corr-recovery", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("last-known-good-load-failed", result.Reason);
        Assert.Equal(["pkg-a"], result.FailedPackageIds);
        Assert.Contains("startup-lkg-recovery-failed:last-known-good-load-failed", startupRecoveryState.GetContribution().DegradedReasons);
    }

    [Fact]
    public async Task LastKnownGoodRecovery_WhenStaleGraphReferencesMissingPackage_IgnoresStaleGraph()
    {
        using var installRoot = new TempDirectory();
        var staleGraph = new GraphActivationRecord(
            "graph-a",
            "gen-stale",
            ["missing-pkg"],
            ["missing-pkg"],
            DateTimeOffset.UtcNow,
            "corr-seed",
            GraphActivationStatus.Stale,
            NodeVersionsByPackageId: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["missing-pkg"] = "9.0.0"
            });
        var (store, _) = await CreateValidLastKnownGoodStoreAsync(
            installRoot,
            new Dictionary<string, GraphActivationRecord>(StringComparer.OrdinalIgnoreCase)
            {
                [staleGraph.GraphId] = staleGraph
            });
        var recovery = new LastKnownGoodStartupRecoveryService(store, new RecordingObserverDispatcher(), new StartupRecoveryState());

        var result = await recovery.TryRecoverAsync("corr-recovery", CancellationToken.None);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task LastKnownGoodRecovery_WhenActiveGraphVersionMismatches_ReturnsFailure()
    {
        using var installRoot = new TempDirectory();
        var activeGraph = new GraphActivationRecord(
            "graph-a",
            "gen-active",
            ["pkg-a"],
            ["pkg-a"],
            DateTimeOffset.UtcNow,
            "corr-seed",
            GraphActivationStatus.Active,
            NodeVersionsByPackageId: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a"] = "2.0.0"
            });
        var (store, _) = await CreateValidLastKnownGoodStoreAsync(
            installRoot,
            new Dictionary<string, GraphActivationRecord>(StringComparer.OrdinalIgnoreCase)
            {
                [activeGraph.GraphId] = activeGraph
            });
        var recovery = new LastKnownGoodStartupRecoveryService(store, new RecordingObserverDispatcher(), new StartupRecoveryState());

        var result = await recovery.TryRecoverAsync("corr-recovery", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("last-known-good-invalid", result.Reason);
        Assert.Equal(["pkg-a"], result.FailedPackageIds);
    }

    [Fact]
    public async Task StartupCycleFailurePolicyUnknownValue_WhenStartupCompletesDegraded_ThrowsNotSupported()
    {
        var service = new DegradedStartupReconciliationService();
        var options = new ReconciliationOptions
        {
            StartupFailurePolicy = (StartupFailurePolicy)999
        };
        var (dispatcher, scheduler, startup) = CreateHostedServices(service, options);

        await dispatcher.StartAsync(CancellationToken.None);

        try
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => startup.StartAsync(CancellationToken.None));
        }
        finally
        {
            await StopHostedServicesAsync(scheduler, dispatcher);
        }
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

    private static (ReconciliationTriggerDispatcherHostedService Dispatcher, ReconciliationHostedService Scheduler, NuplaneStartupHostedService Startup) CreateHostedServices(
        IReconciliationService reconciliationService,
        ReconciliationOptions? options = null,
        ILastKnownGoodStartupRecoveryService? lastKnownGoodStartupRecovery = null)
    {
        options ??= new()
        {
            EnableAutomaticReconciliation = true,
            PollInterval = TimeSpan.FromHours(1)
        };

        var queue = new ReconciliationTriggerQueue();
        var metrics = new ReconciliationMetrics(new());

        return (
            new(
                queue,
                reconciliationService,
                metrics,
                NullLogger<ReconciliationTriggerDispatcherHostedService>.Instance),
            new(
                queue,
                new OptionsWrapper<ReconciliationOptions>(options),
                new OptionsWrapper<ConvergenceOptions>(new()),
                NullLogger<ReconciliationHostedService>.Instance),
            new(
                queue,
                new OptionsWrapper<ReconciliationOptions>(options),
                NullLogger<NuplaneStartupHostedService>.Instance,
                lastKnownGoodStartupRecovery));
    }

    private static async Task<(StoreRegistry Store, string PackageInstallPath)> CreateValidLastKnownGoodStoreAsync(
        TempDirectory installRoot,
        IReadOnlyDictionary<string, GraphActivationRecord>? activeGraphs = null)
    {
        var packageInstallPath = Path.Combine(installRoot.Path, "pkg-a", "1.0.0");
        Directory.CreateDirectory(packageInstallPath);

        var store = new StoreRegistry(new StoreStateSerializer(), stateFilePath: null);
        await store.PersistActiveVersionsAsync(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a"] = "1.0.0"
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a"] = "1.0.0"
            },
            "corr-seed",
            CancellationToken.None,
            new Dictionary<string, ActivePackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a"] = new(
                    "pkg-a",
                    "1.0.0",
                    "local-cache",
                    "desired-source",
                    packageInstallPath,
                    DateTimeOffset.UtcNow,
                    "corr-seed")
            },
            activeGraphs);

        return (store, packageInstallPath);
    }

    private static async Task StopHostedServicesAsync(ReconciliationHostedService scheduler, ReconciliationTriggerDispatcherHostedService dispatcher)
    {
        await scheduler.StopAsync(CancellationToken.None);
        await dispatcher.StopAsync(CancellationToken.None);
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

    private sealed class TrackingReconciliationService : IReconciliationService
    {
        private readonly TaskCompletionSource<TriggerType> _firstTrigger = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TriggerType> WaitForFirstTriggerAsync() => _firstTrigger.Task;

        public Task<ReconciliationRunResult> TriggerAsync(ReconciliationTrigger trigger, CancellationToken cancellationToken)
        {
            _firstTrigger.TrySetResult(trigger.Type);
            return Task.FromResult(new ReconciliationRunResult(false, EmptyChangeSet, [], false));
        }
    }

    private sealed class BlockingCancellationAwareReconciliationService : IReconciliationService
    {
        private int _startedCount;
        private int _cancellationObserved;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObservedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved => Volatile.Read(ref _cancellationObserved) == 1;

        public Task WaitUntilStartedAsync() => _started.Task;

        public Task WaitForCancellationObservedAsync() => _cancellationObservedSource.Task;

        public async Task<ReconciliationRunResult> TriggerAsync(ReconciliationTrigger trigger, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _startedCount);
            _started.TrySetResult();

            try
            {
                await _never.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _cancellationObserved, 1);
                _cancellationObservedSource.TrySetResult();
                throw;
            }

            return new(false, EmptyChangeSet, [], false);
        }
    }

    private sealed class DegradedStartupReconciliationService : IReconciliationService
    {
        public Task<ReconciliationRunResult> TriggerAsync(ReconciliationTrigger trigger, CancellationToken cancellationToken) =>
            Task.FromResult(new ReconciliationRunResult(false, EmptyChangeSet, ["pkg-failed"], true));
    }

    private sealed class FailsFirstReconciliationService : IReconciliationService
    {
        private int _callCount;
        private readonly TaskCompletionSource _secondCall = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public Task WaitForSecondCallAsync() => _secondCall.Task;

        public Task<ReconciliationRunResult> TriggerAsync(ReconciliationTrigger trigger, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _callCount);
            if (count == 2)
            {
                _secondCall.TrySetResult();
            }

            if (count == 1)
            {
                throw new InvalidOperationException("Startup boom");
            }

            return Task.FromResult(new ReconciliationRunResult(false, EmptyChangeSet, [], false));
        }
    }

    private sealed class RecordingCycleFailureContributor : ICycleFailureContributor
    {
        private readonly Dictionary<string, List<string>> _failedPackageIdsByCorrelation = new(StringComparer.OrdinalIgnoreCase);

        public void RecordFailure(string correlationId, string packageId)
        {
            if (!_failedPackageIdsByCorrelation.TryGetValue(correlationId, out var packageIds))
            {
                packageIds = [];
                _failedPackageIdsByCorrelation[correlationId] = packageIds;
            }

            packageIds.Add(packageId);
        }

        public IReadOnlyList<string> TakeFailedPackageIds(string correlationId)
        {
            if (!_failedPackageIdsByCorrelation.Remove(correlationId, out var packageIds))
            {
                return [];
            }

            return packageIds
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private sealed class RecordingObserverDispatcher(
        Action<PackageChangeSet, IReadOnlyList<ResolvedPackage>>? onReconciled = null) : IObserverEventDispatcher
    {
        public List<ResolvedPackage> ReconciledPackages { get; } = [];

        public Task PublishChangingAsync(PackageChangeSet changeSet, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PublishChangedAsync(PackageChangeSet changeSet, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task NotifyPackageFailedAsync(
            string packageId,
            Exception exception,
            string correlationId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PublishReconciledAsync(
            PackageChangeSet changeSet,
            IReadOnlyList<ResolvedPackage> appliedPackages,
            CancellationToken cancellationToken)
        {
            onReconciled?.Invoke(changeSet, appliedPackages);
            ReconciledPackages.AddRange(appliedPackages);
            return Task.CompletedTask;
        }
    }

    #endregion
}
