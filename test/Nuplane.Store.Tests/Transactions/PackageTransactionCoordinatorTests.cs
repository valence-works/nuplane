using Nuplane.Store.Activation;
using Nuplane.Store.State;
using Nuplane.Store.Transactions;

namespace Nuplane.Store.Tests.Transactions;

public sealed class PackageTransactionCoordinatorTests
{
    [Fact]
    public async Task ExecuteAsync_WhenValidateFails_PreservesLastKnownGoodPointer()
    {
        var stateFilePath = Path.Combine(Path.GetTempPath(), $"nuplane-store-{Guid.NewGuid():N}", "state.json");
        var registry = new StoreRegistry(new StoreStateSerializer(), stateFilePath);
        var seedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["pkg-a"] = "1.0.0" };
        await registry.PersistActiveVersionsAsync(
            seedVersions,
            seedVersions,
            "corr-seed",
            CancellationToken.None);

        var pointerSwitcher = new AtomicPointerSwitcher();
        await pointerSwitcher.SwitchAsync("pkg-a", "1.0.0", CancellationToken.None);

        var recorder = new FailureRecorder(registry);
        var coordinator = new PackageTransactionCoordinator(pointerSwitcher, recorder);

        var result = await coordinator.ExecuteAsync(
            new(
                "pkg-a",
                "2.0.0",
                "corr-1",
                StageExecutor: (stage, ct) =>
                {
                    if (stage == PackageTransactionStage.Validate)
                    {
                        throw new InvalidOperationException("validation failed");
                    }

                    return Task.CompletedTask;
                }),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PackageTransactionStage.Validate, result.FailedStage);
        Assert.True(result.LastKnownGoodPreserved);
        Assert.Equal("1.0.0", pointerSwitcher.GetCurrentVersion("pkg-a"));

        var state = await registry.GetStateAsync(CancellationToken.None);
        Assert.True(state.LastFailureById.ContainsKey("pkg-a"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenStageFails_PreservesLastKnownGoodPointer()
    {
        var (pointerSwitcher, recorder, coordinator) = await SetupCoordinator("pkg-b", "1.0.0");

        var result = await coordinator.ExecuteAsync(
            new("pkg-b", "2.0.0", "corr-stage",
                StageExecutor: (stage, _) => stage == PackageTransactionStage.Stage
                    ? throw new InvalidOperationException("staging failed")
                    : Task.CompletedTask),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PackageTransactionStage.Stage, result.FailedStage);
        Assert.True(result.LastKnownGoodPreserved);
        Assert.Equal("1.0.0", pointerSwitcher.GetCurrentVersion("pkg-b"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenPublishImmutableFails_PreservesLastKnownGoodPointer()
    {
        var (pointerSwitcher, recorder, coordinator) = await SetupCoordinator("pkg-c", "1.0.0");

        var result = await coordinator.ExecuteAsync(
            new("pkg-c", "3.0.0", "corr-publish",
                StageExecutor: (stage, _) => stage == PackageTransactionStage.PublishImmutable
                    ? throw new InvalidOperationException("publish failed")
                    : Task.CompletedTask),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PackageTransactionStage.PublishImmutable, result.FailedStage);
        Assert.True(result.LastKnownGoodPreserved);
        Assert.Equal("1.0.0", pointerSwitcher.GetCurrentVersion("pkg-c"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenAtomicSwitchFails_PreservesLastKnownGoodPointer()
    {
        var (pointerSwitcher, recorder, coordinator) = await SetupCoordinator("pkg-d", "1.0.0");

        var result = await coordinator.ExecuteAsync(
            new("pkg-d", "4.0.0", "corr-switch",
                StageExecutor: (stage, _) => stage == PackageTransactionStage.AtomicSwitch
                    ? throw new InvalidOperationException("switch failed")
                    : Task.CompletedTask),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PackageTransactionStage.AtomicSwitch, result.FailedStage);
        Assert.True(result.LastKnownGoodPreserved);
        Assert.Equal("1.0.0", pointerSwitcher.GetCurrentVersion("pkg-d"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenPersistStateFails_PreservesLastKnownGoodPointer()
    {
        var (pointerSwitcher, recorder, coordinator) = await SetupCoordinator("pkg-e", "1.0.0");

        var result = await coordinator.ExecuteAsync(
            new("pkg-e", "5.0.0", "corr-persist",
                StageExecutor: (stage, _) => stage == PackageTransactionStage.PersistState
                    ? throw new InvalidOperationException("persist failed")
                    : Task.CompletedTask),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PackageTransactionStage.PersistState, result.FailedStage);
        Assert.True(result.LastKnownGoodPreserved);
        // PersistState failure rolls back the pointer switch
        Assert.Equal("1.0.0", pointerSwitcher.GetCurrentVersion("pkg-e"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenBlockedByTrustPolicy_PreservesLastKnownGoodAndRecordsFailure()
    {
        var (pointerSwitcher, recorder, coordinator) = await SetupCoordinator("pkg-f", "1.0.0");

        var result = await coordinator.ExecuteAsync(
            new("pkg-f", "6.0.0", "corr-trust",
                BlockedByTrustPolicy: true,
                PolicyFailureMessage: "untrusted feed"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PackageTransactionStage.TrustPolicyGate, result.FailedStage);
        Assert.True(result.LastKnownGoodPreserved);
        Assert.Equal("1.0.0", pointerSwitcher.GetCurrentVersion("pkg-f"));
        Assert.Equal("untrusted feed", result.FailureMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WhenBlockedByLockPolicy_PreservesLastKnownGoodAndRecordsFailure()
    {
        var (pointerSwitcher, recorder, coordinator) = await SetupCoordinator("pkg-g", "1.0.0");

        var result = await coordinator.ExecuteAsync(
            new("pkg-g", "7.0.0", "corr-lock",
                BlockedByLockPolicy: true,
                PolicyFailureMessage: "lock file enforce failure"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PackageTransactionStage.LockFileGate, result.FailedStage);
        Assert.True(result.LastKnownGoodPreserved);
        Assert.Equal("1.0.0", pointerSwitcher.GetCurrentVersion("pkg-g"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenHashMismatch_PreservesLastKnownGoodAndRecordsFailure()
    {
        var (pointerSwitcher, recorder, coordinator) = await SetupCoordinator("pkg-h", "1.0.0");

        var result = await coordinator.ExecuteAsync(
            new("pkg-h", "8.0.0", "corr-hash",
                ExpectedArtifactHash: "sha256-aaa",
                ActualArtifactHash: "sha256-bbb"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PackageTransactionStage.LockFileGate, result.FailedStage);
        Assert.True(result.LastKnownGoodPreserved);
        Assert.Equal("1.0.0", pointerSwitcher.GetCurrentVersion("pkg-h"));
        Assert.Contains("hash mismatch", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoExistingVersion_FailedStageDoesNotPreserveLKG()
    {
        var stateFilePath = Path.Combine(Path.GetTempPath(), $"nuplane-store-{Guid.NewGuid():N}", "state.json");
        var registry = new StoreRegistry(new StoreStateSerializer(), stateFilePath);
        var pointerSwitcher = new AtomicPointerSwitcher();
        var recorder = new FailureRecorder(registry);
        var coordinator = new PackageTransactionCoordinator(pointerSwitcher, recorder);

        var result = await coordinator.ExecuteAsync(
            new("pkg-new", "1.0.0", "corr-new",
                StageExecutor: (stage, _) => stage == PackageTransactionStage.Validate
                    ? throw new InvalidOperationException("validate failed")
                    : Task.CompletedTask),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.LastKnownGoodPreserved);
        Assert.Null(pointerSwitcher.GetCurrentVersion("pkg-new"));
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
