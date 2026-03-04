using Nuplane.Loading;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class UnloadPendingRetryTests
{
    [Fact]
    public async Task AttemptUnloadAsync_RepeatedAttempts_IncrementsAttemptNumber()
    {
        var coordinator = new PackageUnloadCoordinator();
        var context = CreateContext();

        var (_, first) = await coordinator.AttemptUnloadAsync("pkg-a", context, TimeSpan.FromMilliseconds(1), "corr-1", CancellationToken.None);
        var (_, second) = await coordinator.AttemptUnloadAsync("pkg-a", context, TimeSpan.FromMilliseconds(1), "corr-2", CancellationToken.None);

        Assert.True(second.AttemptNumber > first.AttemptNumber);
    }

    private static PackageAssemblyLoadContext CreateContext()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-unload-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourceAssembly = typeof(PackageLoader).Assembly.Location;
        var targetAssembly = Path.Combine(root, Path.GetFileName(sourceAssembly));
        File.Copy(sourceAssembly, targetAssembly, overwrite: true);

        return new(targetAssembly, [], new());
    }
}
