using Nuplane.Store.Activation;
using Nuplane.Store.State;
using Nuplane.Store.Transactions;

namespace Nuplane.Store.Tests.Transactions;

/// <summary>
/// Regression tests proving module-registration surface refactors do not alter
/// the transactional safety or LKG fallback behavior of <see cref="PackageTransactionCoordinator"/>.
/// These tests exercise the same stage-failure and policy-gate paths already covered by
/// the existing coordinator tests, but frame assertions around the invariants that must
/// hold regardless of how registration services compose the runtime service graph.
/// </summary>
public sealed class PackageTransactionCoordinatorRegressionTests
{
    [Fact]
    public async Task ExecuteAsync_SuccessfulTransaction_AdvancesPointerDeterministically()
    {
        var (pointerSwitcher, _, coordinator) = await SetupCoordinator("pkg-success", "1.0.0");

        var result = await coordinator.ExecuteAsync(
            new("pkg-success", "2.0.0", "corr-ok",
                StageExecutor: (_, _) => Task.CompletedTask),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("2.0.0", pointerSwitcher.GetCurrentVersion("pkg-success"));
    }

    [Fact]
    public async Task ExecuteAsync_SequentialTransactions_EachAdvancesPointerDeterministically()
    {
        var (pointerSwitcher, _, coordinator) = await SetupCoordinator("pkg-seq", "1.0.0");

        await coordinator.ExecuteAsync(
            new("pkg-seq", "2.0.0", "corr-1", StageExecutor: (_, _) => Task.CompletedTask),
            CancellationToken.None);

        var result = await coordinator.ExecuteAsync(
            new("pkg-seq", "3.0.0", "corr-2", StageExecutor: (_, _) => Task.CompletedTask),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("3.0.0", pointerSwitcher.GetCurrentVersion("pkg-seq"));
    }

    [Fact]
    public async Task ExecuteAsync_FailureAfterSuccess_RollsBackToLastSuccessfulVersion()
    {
        var (pointerSwitcher, _, coordinator) = await SetupCoordinator("pkg-rollback", "1.0.0");

        await coordinator.ExecuteAsync(
            new("pkg-rollback", "2.0.0", "corr-ok", StageExecutor: (_, _) => Task.CompletedTask),
            CancellationToken.None);

        var failResult = await coordinator.ExecuteAsync(
            new("pkg-rollback", "3.0.0", "corr-fail",
                StageExecutor: (stage, _) => stage == PackageTransactionStage.Validate
                    ? throw new InvalidOperationException("validation failed")
                    : Task.CompletedTask),
            CancellationToken.None);

        Assert.False(failResult.Succeeded);
        Assert.True(failResult.LastKnownGoodPreserved);
        Assert.Equal("2.0.0", pointerSwitcher.GetCurrentVersion("pkg-rollback"));
    }

    [Fact]
    public async Task ExecuteAsync_PolicyGateFailure_DoesNotAlterExistingState()
    {
        var stateFilePath = Path.Combine(Path.GetTempPath(), $"nuplane-store-{Guid.NewGuid():N}", "state.json");
        var registry = new StoreRegistry(new StoreStateSerializer(), stateFilePath);
        var seedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["pkg-policy"] = "1.0.0"
        };
        await registry.PersistActiveVersionsAsync(seedVersions, seedVersions, "corr-seed", CancellationToken.None);

        var pointerSwitcher = new AtomicPointerSwitcher();
        await pointerSwitcher.SwitchAsync("pkg-policy", "1.0.0", CancellationToken.None);

        var recorder = new FailureRecorder(registry);
        var coordinator = new PackageTransactionCoordinator(pointerSwitcher, recorder);

        var result = await coordinator.ExecuteAsync(
            new("pkg-policy", "2.0.0", "corr-trust",
                BlockedByTrustPolicy: true,
                PolicyFailureMessage: "untrusted source"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.LastKnownGoodPreserved);
        Assert.Equal("1.0.0", pointerSwitcher.GetCurrentVersion("pkg-policy"));

        var state = await registry.GetStateAsync(CancellationToken.None);
        Assert.True(state.LastFailureById.ContainsKey("pkg-policy"));
    }

    private static async Task<(AtomicPointerSwitcher, FailureRecorder, PackageTransactionCoordinator)> SetupCoordinator(
        string packageId, string seedVersion)
    {
        var stateFilePath = Path.Combine(Path.GetTempPath(), $"nuplane-store-{Guid.NewGuid():N}", "state.json");
        var registry = new StoreRegistry(new StoreStateSerializer(), stateFilePath);
        var seedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [packageId] = seedVersion };
        await registry.PersistActiveVersionsAsync(seedVersions, seedVersions, $"corr-seed-{packageId}", CancellationToken.None);
        var pointerSwitcher = new AtomicPointerSwitcher();
        await pointerSwitcher.SwitchAsync(packageId, seedVersion, CancellationToken.None);
        var recorder = new FailureRecorder(registry);
        var coordinator = new PackageTransactionCoordinator(pointerSwitcher, recorder);
        return (pointerSwitcher, recorder, coordinator);
    }
}
