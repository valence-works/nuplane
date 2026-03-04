using System.Runtime.CompilerServices;
using Nuplane.Loading;
using Nuplane.Loading.Tests.Fixtures;

namespace Nuplane.Loading.Tests;

public sealed class PackageUnloadCoordinatorTests : IDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("nuplane-unload-test-");
    private readonly PackageUnloadCoordinator _sut = new();

    public void Dispose() => _tempDir.Delete(recursive: true);

    [Fact]
    public async Task AttemptUnloadAsync_ValidContext_ReturnsWithoutException()
    {
        var (handle, _) = CreateHandle("my-pkg");

        var (deactivation, unload) = await _sut.AttemptUnloadAsync(
            "my-pkg", handle, TimeSpan.FromMilliseconds(50), "test", CancellationToken.None);

        Assert.NotNull(deactivation);
        Assert.NotNull(unload);
        Assert.Equal("my-pkg", deactivation.PackageId);
    }

    [Fact]
    public async Task AttemptUnloadAsync_DeactivationTimeout_TimedOutIsTrue()
    {
        var (handle, _) = CreateHandle("my-pkg");

        // Very short timeout — will always time out
        var (deactivation, _) = await _sut.AttemptUnloadAsync(
            "my-pkg", handle, TimeSpan.FromMilliseconds(10), "test", CancellationToken.None);

        Assert.True(deactivation.TimedOut);
        Assert.False(deactivation.Completed);
    }

    [Fact]
    public async Task AttemptUnloadAsync_OutcomeIsNotFailed()
    {
        var (handle, _) = CreateHandle("my-pkg");

        var (_, unload) = await _sut.AttemptUnloadAsync(
            "my-pkg", handle, TimeSpan.FromMilliseconds(25), "test", CancellationToken.None);

        // Outcome may be Unloaded or UnloadPending depending on GC — but never Failed
        Assert.NotEqual(UnloadOutcome.Failed, unload.Outcome);
        Assert.Equal("my-pkg", unload.PackageId);
    }

    [Fact]
    public async Task AttemptUnloadAsync_CalledTwiceWithSameHandle_SecondCallDoesNotThrow()
    {
        var (handle, _) = CreateHandle("my-pkg");

        await _sut.AttemptUnloadAsync("my-pkg", handle, TimeSpan.FromMilliseconds(25), "test", CancellationToken.None);

        // Second call should not throw (double Unload is a no-op at the ALC level)
        var ex = await Record.ExceptionAsync(() =>
            _sut.AttemptUnloadAsync("my-pkg", handle, TimeSpan.FromMilliseconds(25), "test", CancellationToken.None));

        Assert.Null(ex);
    }

    private (PackageLoadContextHandle handle, PackageAssemblyLoadContext ctx) CreateHandle(string packageId)
    {
        var assemblyPath = CopyFixtureAssembly(packageId);
        var ctx = CreateContext(assemblyPath);
        var handle = new PackageLoadContextHandle($"{packageId}@1.0.0", ctx);
        return (handle, ctx);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PackageAssemblyLoadContext CreateContext(string assemblyPath) =>
        new(assemblyPath, [], new SharedAssemblyPolicyMatcher());

    private string CopyFixtureAssembly(string pkgName)
    {
        var dir = _tempDir.CreateSubdirectory(pkgName);
        var src = typeof(FixtureMarker).Assembly.Location;
        var dest = Path.Combine(dir.FullName, Path.GetFileName(src));
        File.Copy(src, dest);
        return dest;
    }
}
