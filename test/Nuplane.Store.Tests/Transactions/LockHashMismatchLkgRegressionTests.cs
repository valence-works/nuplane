using Nuplane.Store.Activation;
using Nuplane.Store.State;
using Nuplane.Store.Transactions;

namespace Nuplane.Store.Tests.Transactions;

public sealed class LockHashMismatchLkgRegressionTests
{
    [Fact]
    public async Task ExecuteAsync_WhenLockHashMismatch_PreservesLkgActivePointer()
    {
        var stateFilePath = Path.Combine(Path.GetTempPath(), $"nuplane-store-{Guid.NewGuid():N}", "state.json");
        var registry = new StoreRegistry(new StoreStateSerializer(), stateFilePath);
        var seed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["pkg-a"] = "1.0.0" };
        await registry.PersistActiveVersionsAsync(seed, seed, "corr-seed", CancellationToken.None);

        var pointerSwitcher = new AtomicPointerSwitcher();
        await pointerSwitcher.SwitchAsync("pkg-a", "1.0.0", CancellationToken.None);

        var coordinator = new PackageTransactionCoordinator(pointerSwitcher, new FailureRecorder(registry));
        var result = await coordinator.ExecuteAsync(
            new PackageTransactionRequest(
                PackageId: "pkg-a",
                Version: "2.0.0",
                CorrelationId: "corr-lock",
                ExpectedArtifactHash: "sha512:expected",
                ActualArtifactHash: "sha512:actual"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PackageTransactionStage.LockFileGate, result.FailedStage);
        Assert.True(result.LastKnownGoodPreserved);
        Assert.Equal("1.0.0", pointerSwitcher.GetCurrentVersion("pkg-a"));
    }
}
