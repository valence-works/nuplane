using Nuplane.Loading;

namespace Nuplane.Integration.Tests.Contracts;

public sealed class UnloadLifecycleContractTests
{
    [Fact]
    public async Task AttemptUnloadAsync_TimeoutStillReturnsUnloadOutcome()
    {
        var coordinator = new PackageUnloadCoordinator();
        var context = CreateContext();

        var (deactivation, unload) = await coordinator.AttemptUnloadAsync(
            "pkg-timeout",
            context,
            TimeSpan.FromMilliseconds(50),
            "corr-timeout",
            CancellationToken.None);

        Assert.True(deactivation.TimedOut);
        Assert.True(unload.Outcome == UnloadOutcome.Unloaded || unload.Outcome == UnloadOutcome.UnloadPending);
    }

    private static PackageAssemblyLoadContext CreateContext()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-unload-contract-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourceAssembly = typeof(PackageLoader).Assembly.Location;
        var targetAssembly = Path.Combine(root, Path.GetFileName(sourceAssembly));
        File.Copy(sourceAssembly, targetAssembly, overwrite: true);

        return new PackageAssemblyLoadContext(targetAssembly, [], new SharedAssemblyPolicyMatcher());
    }
}
