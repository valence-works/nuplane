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
            new PackageTransactionRequest(
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
}
