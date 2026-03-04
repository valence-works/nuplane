using Nuplane.Abstractions;
using Nuplane.Loading;
using Nuplane.Loading.Configuration;
using Nuplane.Loading.Hosting;
using Nuplane.Runtime.Loading;

namespace Nuplane.Integration.Tests.Loading;

/// <summary>
/// T040 — Integration test verifying that a known package type can be loaded
/// from an active package when the loader is enabled via the loader boundary.
/// </summary>
public sealed class LoaderActivatedPackageIntegrationTests : IDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("nuplane-loader-int-");

    public void Dispose() => _tempDir.Delete(recursive: true);

    [Fact]
    public async Task EnabledLoader_ValidPackage_ReturnsLoadedOutcome()
    {
        // Create a directory that serves as the package install path with a real assembly.
        var installPath = Path.Combine(_tempDir.FullName, "pkg-a", "1.0.0");
        Directory.CreateDirectory(installPath);
        CopyFixtureAssembly(installPath);

        var options = new LoadingOptions { Enabled = true };
        var loader = new PackageLoader();
        var adapter = new NuplaneLoadingAdapter(options, loader);

        var packages = new[]
        {
            new ResolvedPackage("pkg-a", "1.0.0", "feed-1", installPath, DateTimeOffset.UtcNow)
        };

        var result = await adapter.LoadAsync(packages, "corr-1", CancellationToken.None);

        Assert.Single(result.Entries);
        Assert.Equal(PackageLoaderOutcome.Loaded, result.Entries[0].Outcome);
        Assert.Equal("pkg-a", result.Entries[0].PackageId);
        Assert.Null(result.Entries[0].ReasonCode);
    }

    [Fact]
    public async Task EnabledLoader_MultiplePackages_EachGetIndependentOutcome()
    {
        var installA = Path.Combine(_tempDir.FullName, "pkg-a", "1.0.0");
        var installB = Path.Combine(_tempDir.FullName, "pkg-b", "2.0.0");
        Directory.CreateDirectory(installA);
        Directory.CreateDirectory(installB);
        CopyFixtureAssembly(installA);
        CopyFixtureAssembly(installB);

        var options = new LoadingOptions { Enabled = true };
        var loader = new PackageLoader();
        var adapter = new NuplaneLoadingAdapter(options, loader);

        var packages = new[]
        {
            new ResolvedPackage("pkg-a", "1.0.0", "feed-1", installA, DateTimeOffset.UtcNow),
            new ResolvedPackage("pkg-b", "2.0.0", "feed-1", installB, DateTimeOffset.UtcNow)
        };

        var result = await adapter.LoadAsync(packages, "corr-1", CancellationToken.None);

        Assert.Equal(2, result.Entries.Count);
        Assert.All(result.Entries, e => Assert.Equal(PackageLoaderOutcome.Loaded, e.Outcome));
    }

    [Fact]
    public async Task DisabledLoader_ReturnsSkippedForAllPackages()
    {
        var installPath = Path.Combine(_tempDir.FullName, "pkg-a", "1.0.0");
        Directory.CreateDirectory(installPath);

        var options = new LoadingOptions { Enabled = false };
        var adapter = new NuplaneLoadingAdapter(options, new PackageLoader());

        var packages = new[]
        {
            new ResolvedPackage("pkg-a", "1.0.0", "feed-1", installPath, DateTimeOffset.UtcNow)
        };

        var result = await adapter.LoadAsync(packages, "corr-1", CancellationToken.None);

        Assert.Single(result.Entries);
        Assert.Equal(PackageLoaderOutcome.Skipped, result.Entries[0].Outcome);
    }

    private static void CopyFixtureAssembly(string targetDir)
    {
        var sourceAssembly = typeof(PackageLoader).Assembly.Location;
        File.Copy(sourceAssembly, Path.Combine(targetDir, Path.GetFileName(sourceAssembly)), overwrite: true);
    }
}
