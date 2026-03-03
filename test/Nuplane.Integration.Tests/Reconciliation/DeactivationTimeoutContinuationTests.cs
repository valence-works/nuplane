using Nuplane.Loading;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class DeactivationTimeoutContinuationTests
{
    [Fact]
    public async Task AttemptUnloadAsync_WhenDeactivationTimeoutOccurs_StillAttemptsUnload()
    {
        var coordinator = new PackageUnloadCoordinator();
        var context = CreateContext();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (_, unload) = await coordinator.AttemptUnloadAsync(
            "pkg-timeout",
            context,
            TimeSpan.FromSeconds(1),
            "corr-timeout",
            cts.Token);

        Assert.True(unload.AttemptNumber >= 1);
    }

    private static PackageAssemblyLoadContext CreateContext()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-timeout-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourceAssembly = typeof(PackageLoader).Assembly.Location;
        var targetAssembly = Path.Combine(root, Path.GetFileName(sourceAssembly));
        File.Copy(sourceAssembly, targetAssembly, overwrite: true);

        return new PackageAssemblyLoadContext(targetAssembly, [], new SharedAssemblyPolicyMatcher());
    }
}
