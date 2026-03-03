using Nuplane.Loading;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class UnloadPendingRecoveryTests
{
    [Fact]
    public async Task AttemptUnloadAsync_MultipleCycles_RemainsRetryEligible()
    {
        var coordinator = new PackageUnloadCoordinator();
        var context = CreateContext();

        var (_, first) = await coordinator.AttemptUnloadAsync("pkg-recover", context, TimeSpan.FromMilliseconds(1), "corr-1", CancellationToken.None);
        var (_, second) = await coordinator.AttemptUnloadAsync("pkg-recover", context, TimeSpan.FromMilliseconds(1), "corr-2", CancellationToken.None);

        Assert.True(first.RetryEligible);
        Assert.True(second.RetryEligible);
        Assert.True(second.AttemptNumber > first.AttemptNumber);
    }

    private static PackageAssemblyLoadContext CreateContext()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-recovery-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourceAssembly = typeof(PackageLoader).Assembly.Location;
        var targetAssembly = Path.Combine(root, Path.GetFileName(sourceAssembly));
        File.Copy(sourceAssembly, targetAssembly, overwrite: true);

        return new PackageAssemblyLoadContext(targetAssembly, [], new SharedAssemblyPolicyMatcher());
    }
}
