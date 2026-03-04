using Nuplane.Loading;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class DeactivationTimeoutContinuationTests
{
    [Fact]
    public async Task AttemptUnloadAsync_WhenDeactivationTimeoutOccurs_StillAttemptsUnload()
    {
        var coordinator = new PackageUnloadCoordinator();
        var context = CreateContext();

        var (_, unload) = await coordinator.AttemptUnloadAsync(
            "pkg-timeout",
            context,
            TimeSpan.FromMilliseconds(50),
            "corr-timeout",
            CancellationToken.None);

        Assert.True(unload.AttemptNumber >= 1);
    }

    private static PackageAssemblyLoadContext CreateContext()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-timeout-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourceAssembly = typeof(PackageLoader).Assembly.Location;
        var targetAssembly = Path.Combine(root, Path.GetFileName(sourceAssembly));
        File.Copy(sourceAssembly, targetAssembly, overwrite: true);

        return new(targetAssembly, [], new());
    }
}
