using Nuplane.Loading;

namespace Nuplane.Integration.Tests.Contracts;

public sealed class UnloadLifecycleContractTests
{
    [Fact]
    public async Task AttemptUnloadAsync_TimeoutStillReturnsUnloadOutcome()
    {
        var coordinator = new PackageUnloadCoordinator();
        var context = CreateContext();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (deactivation, unload) = await coordinator.AttemptUnloadAsync(
            "pkg-timeout",
            context,
            TimeSpan.FromSeconds(1),
            "corr-timeout",
            cts.Token);

        Assert.True(deactivation.TimedOut);
        Assert.True(unload.RetryEligible);
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
