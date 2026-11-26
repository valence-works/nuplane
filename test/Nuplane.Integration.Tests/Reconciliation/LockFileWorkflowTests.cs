using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.LockFile;
using Nuplane.Reconciliation.Models;
using Nuplane.Sources.Directory.Builder;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class LockFileWorkflowTests : IDisposable
{
    private const string FeedName = "local-lock-test";
    private const string RootPackage = "Lock.Test.Root";
    private const string DependencyPackage = "Lock.Test.Dependency";
    private const string Version = "1.0.0";

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"nuplane-lock-workflow-{Guid.NewGuid():N}");
    private string FeedPath => Path.Combine(_workspace, "feed");
    private string LockPath => Path.Combine(_workspace, "nuplane.lock.json");

    [Theory]
    [InlineData(LockFileMode.Enforce)]
    [InlineData(LockFileMode.Strict)]
    public async Task TriggerAsync_GeneratedLockAfterRestart_AcceptsTheSamePackageGraph(LockFileMode enforcingMode)
    {
        var generated = await GenerateAsync();
        var originalBytes = await File.ReadAllBytesAsync(LockPath);

        await using var restarted = CreateProvider(enforcingMode, "original");
        var repeated = await ReconcileAsync(restarted);

        Assert.False(repeated.Skipped);
        Assert.False(repeated.IsDegraded);
        Assert.Empty(repeated.FailedPackages);
        var active = await ReadActiveAsync(restarted);
        Assert.Equal(
            generated.Packages.Select(entry => (entry.Id, entry.Version, (string?)entry.Hash)),
            active.Packages.Select(package => (package.PackageId, package.Version, package.ArchiveHash)));
        Assert.Equal(RootPackage, Assert.Single(active.Packages, package => package.Discoverable).PackageId);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(LockPath));
    }

    [Theory]
    [InlineData(LockFileMode.Enforce, false)]
    [InlineData(LockFileMode.Strict, true)]
    public async Task TriggerAsync_DependencyLockEntryMissing_AppliesTheSelectedMode(
        LockFileMode enforcingMode, bool rejected)
    {
        var generated = await GenerateAsync();
        await OpenLock().WriteAsync(
            generated with { Packages = generated.Packages.Where(entry => entry.Id == RootPackage).ToArray() },
            CancellationToken.None);

        await using var verifier = CreateProvider(enforcingMode, "missing-entry");
        var result = await ReconcileAsync(verifier);

        Assert.False(result.Skipped);
        Assert.Equal(rejected, result.IsDegraded);
        Assert.Equal(rejected, result.FailedPackages.Contains(RootPackage, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(!rejected, (await ReadActiveAsync(verifier)).Packages.Any());
    }

    [Theory]
    [InlineData(LockFileMode.Enforce)]
    [InlineData(LockFileMode.Strict)]
    public async Task TriggerAsync_DependencyArchiveChanges_RejectsActivation(LockFileMode enforcingMode)
    {
        await GenerateAsync();
        var originalBytes = await File.ReadAllBytesAsync(LockPath);
        WritePackage(DependencyPackage, "different bytes");
        await using var verifier = CreateProvider(enforcingMode, "different-archive");

        var result = await ReconcileAsync(verifier);

        Assert.False(result.Skipped);
        Assert.True(result.IsDegraded);
        Assert.Contains(RootPackage, result.FailedPackages, StringComparer.OrdinalIgnoreCase);
        Assert.Empty((await ReadActiveAsync(verifier)).Packages);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(LockPath));
    }

    [Fact]
    public async Task TriggerAsync_GenerateWithMissingDependency_PreservesPreviousLock()
    {
        await GenerateAsync();
        var originalBytes = await File.ReadAllBytesAsync(LockPath);
        File.Delete(PackagePath(DependencyPackage));
        await using var verifier = CreateProvider(LockFileMode.Generate, "missing-dependency");

        var result = await ReconcileAsync(verifier);

        Assert.False(result.Skipped);
        Assert.True(result.IsDegraded);
        Assert.Contains(RootPackage, result.FailedPackages, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(LockPath));
    }

    private async Task<PackageLockFile> GenerateAsync()
    {
        Directory.CreateDirectory(FeedPath);
        WritePackage(DependencyPackage, "original bytes");
        WritePackage(RootPackage, "root bytes", DependencyPackage);

        await using var provider = CreateProvider(LockFileMode.Generate, "original");
        var result = await ReconcileAsync(provider);

        Assert.False(result.Skipped);
        Assert.False(result.IsDegraded);
        Assert.Empty(result.FailedPackages);

        var generated = await OpenLock().ReadAsync(CancellationToken.None);
        Assert.NotNull(generated);
        Assert.Equal("2.0", generated.SchemaVersion);
        Assert.Equal([DependencyPackage, RootPackage], generated.Packages.Select(entry => entry.Id));
        foreach (var entry in generated.Packages)
        {
            Assert.Equal(FeedName, entry.Feed);
            Assert.Equal(Version, entry.Version);
            var expectedHash = SHA512.HashData(await File.ReadAllBytesAsync(PackagePath(entry.Id)));
            Assert.Equal($"sha512:{Convert.ToBase64String(expectedHash)}", entry.Hash);
        }

        return generated;
    }

    private ServiceProvider CreateProvider(LockFileMode mode, string stateName)
    {
        var root = Path.Combine(_workspace, stateName);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FeedResolution:PackageInstallRoot"] = Path.Combine(root, "installed"),
                ["FeedResolution:OfflineMode"] = "true",
                ["LockFile:Mode"] = mode.ToString(),
                ["LockFile:Path"] = LockPath,
                ["Reconciliation:MaxRetryAttempts"] = "0",
                ["StoreRegistry:StateFilePath"] = Path.Combine(root, "store-state.json"),
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(configuration, nuplane =>
            nuplane.AddDirectoryFeed(FeedName, FeedPath, feed =>
            {
                feed.Trust = FeedTrustLevel.Trusted;
                feed.Include(RootPackage);
                feed.Watch = false;
            }));
        return services.BuildServiceProvider();
    }

    private LockFileStore OpenLock() => new(Options.Create(new LockFileOptions { Path = LockPath }));

    private static Task<ReconciliationRunResult> ReconcileAsync(IServiceProvider provider) =>
        provider.GetRequiredService<IReconciliationService>()
            .TriggerAsync(ReconciliationTrigger.Manual("lock-workflow-test"), CancellationToken.None);

    private static Task<ActivePackagesSnapshot> ReadActiveAsync(IServiceProvider provider) =>
        provider.GetRequiredService<IActivePackageCatalog>().GetActivePackagesAsync(CancellationToken.None);

    private string PackagePath(string id) => Path.Combine(FeedPath, $"{id}.{Version}.nupkg");

    private void WritePackage(string id, string contents, string? dependency = null)
    {
        var dependencies = dependency is null
            ? string.Empty
            : $"""<dependencies><dependency id="{dependency}" version="[{Version}]" /></dependencies>""";

        using var file = File.Create(PackagePath(id));
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry($"{id}.nuspec").Open()))
        {
            writer.Write($"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>{id}</id>
                    <version>{Version}</version>
                    <authors>Nuplane Tests</authors>
                    <description>Lock workflow test package</description>
                    {dependencies}
                  </metadata>
                </package>
                """);
        }

        using var content = new StreamWriter(archive.CreateEntry("content/marker.txt").Open());
        content.Write(contents);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }
}
