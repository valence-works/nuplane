using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nuplane.Abstractions;
using Nuplane.Feeds;
using Nuplane.PackageArchives;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;
using Nuplane.Sources.Directory.Builder;

namespace Nuplane.Integration.Tests.Content;

/// <summary>
/// Proves that a host with no enrolled package store reads an active package's files from its verified archive.
/// </summary>
/// <remarks>
/// <c>NUPLANE_ARCHIVE_TEST_ROOT</c> moves the test directories onto a chosen filesystem, and
/// <c>NUPLANE_EXPECT_FILESYSTEM</c> (Linux) makes the test assert that filesystem's type, so CI can prove the
/// reader on overlayfs, xfs, btrfs and tmpfs.
/// </remarks>
public sealed class PackageArchiveReaderAcceptanceTests : IDisposable
{
    private const string PackageId = "Acme.Studio.Addon";
    private const string ModuleScript = "export const addon = 'acme';";
    private readonly string _root;

    public PackageArchiveReaderAcceptanceTests()
    {
        var baseDirectory = Environment.GetEnvironmentVariable("NUPLANE_ARCHIVE_TEST_ROOT") is { Length: > 0 } configured
            ? configured
            : Path.GetTempPath();
        _root = Path.Combine(baseDirectory, $"nuplane-archive-acceptance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task OpenAsync_UnenrolledHost_ServesActivePackageContentFromVerifiedArchive()
    {
        AssertExpectedFilesystem(_root);
        var feed = WritePackage("1.0.0");
        using var host = CreateHost(feed);
        await ReconcileAsync(host);

        var package = await GetActivePackageAsync(host);
        var archive = await host.Services.GetRequiredService<IPackageArchiveReader>().OpenAsync(package);

        Assert.Equal(PackageArchiveHash(package), package.ArchiveHash);
        Assert.Equal(ModuleScript, Encoding.UTF8.GetString(archive.GetEntry("staticwebassets/module.js")!.ReadAllBytes()));
        Assert.NotNull(archive.Entries.SingleOrDefault(entry => entry.Path == $"{PackageId}.nuspec"));

        // Opening reads only the kept archive: a restarted host serves it with the feed copy gone.
        File.Delete(feed);
        using var restarted = CreateHost(Path.GetDirectoryName(feed)!, createFeedPackage: false);
        var restartedPackage = await GetActivePackageAsync(restarted);
        var reopened = await restarted.Services.GetRequiredService<IPackageArchiveReader>().OpenAsync(restartedPackage);
        Assert.Equal(archive.ArchiveHash, reopened.ArchiveHash);
    }

    [Fact]
    public async Task OpenAsync_UnenrolledHost_RefusesModifiedReplacedOrMissingArchive()
    {
        AssertExpectedFilesystem(_root);
        var feed = WritePackage("1.0.0");
        using var host = CreateHost(feed);
        await ReconcileAsync(host);
        var package = await GetActivePackageAsync(host);
        var archivePath = PackageInstallStore.GetArchivePath(package.InstallPath);
        var original = await File.ReadAllBytesAsync(archivePath);

        // Modified in place.
        var modified = (byte[])original.Clone();
        modified[^12] ^= 0x5A;
        await File.WriteAllBytesAsync(archivePath, modified);
        await AssertRefusedAsync(package, PackageArchiveFailureReason.HashMismatch);

        // Replaced together with the acquisition hash file beside it: the active record still decides.
        var replacement = BuildPackage("1.0.0", "export const addon = 'forged';");
        await File.WriteAllBytesAsync(archivePath, replacement);
        await File.WriteAllTextAsync(Path.Combine(package.InstallPath, PackageInstallStore.ContentHashFileName),
            "sha512:" + Convert.ToBase64String(System.Security.Cryptography.SHA512.HashData(replacement)));
        await AssertRefusedAsync(package, PackageArchiveFailureReason.HashMismatch);

        // A restart and another reconciliation reuse the install; the hash recorded at activation still decides.
        using (var restarted = CreateHost(Path.GetDirectoryName(feed)!, createFeedPackage: false))
        {
            await ReconcileAsync(restarted);
            var reconciled = await GetActivePackageAsync(restarted);
            Assert.Equal(package.ArchiveHash, reconciled.ArchiveHash);
            var error = await Assert.ThrowsAsync<PackageArchiveException>(() =>
                restarted.Services.GetRequiredService<IPackageArchiveReader>().OpenAsync(reconciled).AsTask());
            Assert.Equal(PackageArchiveFailureReason.HashMismatch, error.Reason);
        }

        // Missing.
        File.Delete(archivePath);
        await AssertRefusedAsync(package, PackageArchiveFailureReason.ArchiveMissing);

        // Restored: served again.
        await File.WriteAllBytesAsync(archivePath, original);
        using var reader = CreateHost(Path.GetDirectoryName(feed)!, createFeedPackage: false);
        var archive = await reader.Services.GetRequiredService<IPackageArchiveReader>().OpenAsync(package);
        Assert.Equal(ModuleScript, Encoding.UTF8.GetString(archive.GetEntry("staticwebassets/module.js")!.ReadAllBytes()));

        async Task AssertRefusedAsync(ActivePackage refused, PackageArchiveFailureReason reason)
        {
            // A fresh host has an empty verified-archive cache.
            using var fresh = CreateHost(Path.GetDirectoryName(feed)!, createFeedPackage: false);
            var error = await Assert.ThrowsAsync<PackageArchiveException>(() =>
                fresh.Services.GetRequiredService<IPackageArchiveReader>().OpenAsync(refused).AsTask());
            Assert.Equal(reason, error.Reason);
            Assert.Equal(PackageId, error.PackageId);
            Assert.Contains(PackageId, error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task OpenAsync_RollbackToForgedRetainedInstall_RefusesWithTheHashRecordedAtFirstActivation()
    {
        AssertExpectedFilesystem(_root);
        WritePackage("1.0.0");
        var feedDirectory = Path.Combine(_root, "feed");
        ActivePackage original;
        using (var host = CreateHost(feedDirectory, createFeedPackage: false))
        {
            await ReconcileAsync(host);
            original = await GetActivePackageAsync(host);
            var newer = WritePackage("2.0.0");
            await ReconcileAsync(host);
            Assert.Equal("2.0.0", (await GetActivePackageAsync(host)).Version);

            // Forge the retained 1.0.0 install: its archive and the acquisition hash file beside it.
            var forged = BuildPackage("1.0.0", "export const addon = 'forged';");
            await File.WriteAllBytesAsync(PackageInstallStore.GetArchivePath(original.InstallPath), forged);
            await File.WriteAllTextAsync(Path.Combine(original.InstallPath, PackageInstallStore.ContentHashFileName),
                "sha512:" + Convert.ToBase64String(System.Security.Cryptography.SHA512.HashData(forged)));
            File.Delete(newer);
        }

        // A restarted host rolls back to 1.0.0, which is reused from disk.
        using var restarted = CreateHost(feedDirectory, createFeedPackage: false);
        await ReconcileAsync(restarted);
        var rolledBack = await GetActivePackageAsync(restarted);

        Assert.Equal("1.0.0", rolledBack.Version);
        Assert.Equal(original.InstallPath, rolledBack.InstallPath);
        Assert.Equal(original.ArchiveHash, rolledBack.ArchiveHash);
        var error = await Assert.ThrowsAsync<PackageArchiveException>(() =>
            restarted.Services.GetRequiredService<IPackageArchiveReader>().OpenAsync(rolledBack).AsTask());
        Assert.Equal(PackageArchiveFailureReason.HashMismatch, error.Reason);
    }

    [Fact]
    public async Task OpenAsync_StateWithoutRecordedHashes_RefusesUntilTheNextReconciliationRecordsThem()
    {
        AssertExpectedFilesystem(_root);
        var feed = WritePackage("1.0.0");
        var statePath = Path.Combine(_root, "store-state.json");
        string recordedHash;
        using (var host = CreateHost(feed))
        {
            await ReconcileAsync(host);
            recordedHash = (await GetActivePackageAsync(host)).ArchiveHash!;
        }

        // A state written by a Nuplane version without archive hashes.
        var state = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(statePath))!.AsObject();
        Assert.True(state.Remove("archiveHashesByPackage"));
        foreach (var descriptor in state["activePackageDescriptorsById"]!.AsObject())
            Assert.True(descriptor.Value!.AsObject().Remove("archiveHash"));
        await File.WriteAllTextAsync(statePath, state.ToJsonString());

        using var upgraded = CreateHost(Path.GetDirectoryName(feed)!, createFeedPackage: false);
        var unrecorded = await GetActivePackageAsync(upgraded);
        Assert.Null(unrecorded.ArchiveHash);
        var error = await Assert.ThrowsAsync<PackageArchiveException>(() =>
            upgraded.Services.GetRequiredService<IPackageArchiveReader>().OpenAsync(unrecorded).AsTask());
        Assert.Equal(PackageArchiveFailureReason.HashNotRecorded, error.Reason);

        await ReconcileAsync(upgraded);
        var recorded = await GetActivePackageAsync(upgraded);
        Assert.Equal(recordedHash, recorded.ArchiveHash);
        var archive = await upgraded.Services.GetRequiredService<IPackageArchiveReader>().OpenAsync(recorded);
        Assert.Equal(ModuleScript, Encoding.UTF8.GetString(archive.GetEntry("staticwebassets/module.js")!.ReadAllBytes()));
    }

    [Fact]
    public async Task OpenAsync_ForgedInstallReachedThroughAnotherRootSpelling_RefusesWithTheRecordedHash()
    {
        AssertExpectedFilesystem(_root);
        var feed = WritePackage("1.0.0");
        ActivePackage original;
        using (var host = CreateHost(feed))
        {
            await ReconcileAsync(host);
            original = await GetActivePackageAsync(host);
        }

        var forged = BuildPackage("1.0.0", "export const addon = 'forged';");
        await File.WriteAllBytesAsync(PackageInstallStore.GetArchivePath(original.InstallPath), forged);
        await File.WriteAllTextAsync(Path.Combine(original.InstallPath, PackageInstallStore.ContentHashFileName),
            "sha512:" + Convert.ToBase64String(System.Security.Cryptography.SHA512.HashData(forged)));
        // The same physical install root, spelled differently: a case variant on Windows (links need a
        // privilege there), a link elsewhere.
        string alias;
        if (OperatingSystem.IsWindows())
        {
            alias = Path.Combine(_root, "PACKAGES");
        }
        else
        {
            alias = Path.Combine(_root, "packages-alias");
            Directory.CreateSymbolicLink(alias, Path.Combine(_root, "packages"));
        }

        using var aliased = CreateHost(Path.GetDirectoryName(feed)!, createFeedPackage: false, packageInstallRoot: alias);
        await ReconcileAsync(aliased);
        var reached = await GetActivePackageAsync(aliased);

        Assert.NotEqual(original.InstallPath, reached.InstallPath);
        Assert.Equal(original.ArchiveHash, reached.ArchiveHash);
        var error = await Assert.ThrowsAsync<PackageArchiveException>(() =>
            aliased.Services.GetRequiredService<IPackageArchiveReader>().OpenAsync(reached).AsTask());
        Assert.Equal(PackageArchiveFailureReason.HashMismatch, error.Reason);
    }

    [Fact]
    public async Task OpenAsync_ForgedInstallSharedByARenamedFeed_RefusesWithTheRecordedHash()
    {
        AssertExpectedFilesystem(_root);
        var feed = WritePackage("1.0.0");
        ActivePackage original;
        using (var host = CreateHost(feed, feedName: "add/ons"))
        {
            await ReconcileAsync(host);
            original = await GetActivePackageAsync(host);
        }

        var forged = BuildPackage("1.0.0", "export const addon = 'forged';");
        await File.WriteAllBytesAsync(PackageInstallStore.GetArchivePath(original.InstallPath), forged);
        await File.WriteAllTextAsync(Path.Combine(original.InstallPath, PackageInstallStore.ContentHashFileName),
            "sha512:" + Convert.ToBase64String(System.Security.Cryptography.SHA512.HashData(forged)));

        // "add_ons" sanitizes to the same install directory segment as "add/ons".
        using var renamed = CreateHost(Path.GetDirectoryName(feed)!, createFeedPackage: false, feedName: "add_ons");
        await ReconcileAsync(renamed);
        var reached = await GetActivePackageAsync(renamed);

        Assert.Equal(original.InstallPath, reached.InstallPath);
        Assert.Equal(original.ArchiveHash, reached.ArchiveHash);
        var error = await Assert.ThrowsAsync<PackageArchiveException>(() =>
            renamed.Services.GetRequiredService<IPackageArchiveReader>().OpenAsync(reached).AsTask());
        Assert.Equal(PackageArchiveFailureReason.HashMismatch, error.Reason);
    }

    [Theory]
    [InlineData("addons", "addon\u017F")]          // ſ folds to s on case-insensitive volumes
    [InlineData("caf\u00E9", "cafe\u0301")]       // NFC and NFD spellings of café
    [InlineData("addons", "addons.")]               // Windows drops a trailing dot
    [InlineData("Addons", "addons")]
    [InlineData("strasse", "stra\u00DFe")]           // APFS full case folding: ß and ss
    [InlineData("office", "o\uFB03ce")]              // APFS full case folding: the ﬃ ligature
    public async Task OpenAsync_FeedRenamedToAnEquivalentSpelling_NeverServesAForgedArchive(string before, string after)
    {
        AssertExpectedFilesystem(_root);
        var feed = WritePackage("1.0.0");
        ActivePackage original;
        using (var host = CreateHost(feed, feedName: before))
        {
            await ReconcileAsync(host);
            original = await GetActivePackageAsync(host);
        }

        var forged = BuildPackage("1.0.0", "export const addon = 'forged';");
        await File.WriteAllBytesAsync(PackageInstallStore.GetArchivePath(original.InstallPath), forged);
        await File.WriteAllTextAsync(Path.Combine(original.InstallPath, PackageInstallStore.ContentHashFileName),
            "sha512:" + Convert.ToBase64String(System.Security.Cryptography.SHA512.HashData(forged)));

        using var renamed = CreateHost(Path.GetDirectoryName(feed)!, createFeedPackage: false, feedName: after);
        await ReconcileAsync(renamed);
        var reached = await GetActivePackageAsync(renamed);
        var reader = renamed.Services.GetRequiredService<IPackageArchiveReader>();

        // Whether the filesystem maps the new name onto the forged install or to a fresh extraction, the
        // reader serves the genuine content or refuses; it never serves the forged archive.
        try
        {
            var archive = await reader.OpenAsync(reached);
            Assert.Equal(ModuleScript, Encoding.UTF8.GetString(archive.GetEntry("staticwebassets/module.js")!.ReadAllBytes()));
            Assert.NotEqual(original.InstallPath, reached.InstallPath, StringComparer.Ordinal);
        }
        catch (PackageArchiveException error)
        {
            Assert.Equal(PackageArchiveFailureReason.HashMismatch, error.Reason);
            Assert.Equal(original.ArchiveHash, reached.ArchiveHash);
        }
    }

    [Fact]
    public async Task OpenAsync_VersionServedFromAnotherFeedOverAForgedInstall_RefusesWithTheRecordedHash()
    {
        AssertExpectedFilesystem(_root);
        var feed = WritePackage("1.0.0");
        ActivePackage original;
        using (var host = CreateHost(feed))
        {
            await ReconcileAsync(host);
            original = await GetActivePackageAsync(host);
        }

        // A forged install planted where another feed would install the same version.
        var mirrorInstall = PackageInstallStore.GetInstallDirectory(Path.Combine(_root, "packages"), "mirror", PackageId, "1.0.0");
        Directory.CreateDirectory(mirrorInstall);
        foreach (var file in Directory.EnumerateFiles(original.InstallPath, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(mirrorInstall, Path.GetRelativePath(original.InstallPath, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        var forged = BuildPackage("1.0.0", "export const addon = 'forged';");
        await File.WriteAllBytesAsync(PackageInstallStore.GetArchivePath(mirrorInstall), forged);
        await File.WriteAllTextAsync(Path.Combine(mirrorInstall, PackageInstallStore.ContentHashFileName),
            "sha512:" + Convert.ToBase64String(System.Security.Cryptography.SHA512.HashData(forged)));

        using var mirror = CreateHost(Path.GetDirectoryName(feed)!, createFeedPackage: false, feedName: "mirror");
        await ReconcileAsync(mirror);
        var reached = await GetActivePackageAsync(mirror);

        Assert.Equal(Path.GetFullPath(mirrorInstall), Path.GetFullPath(reached.InstallPath));
        Assert.Equal(original.ArchiveHash, reached.ArchiveHash);
        var error = await Assert.ThrowsAsync<PackageArchiveException>(() =>
            mirror.Services.GetRequiredService<IPackageArchiveReader>().OpenAsync(reached).AsTask());
        Assert.Equal(PackageArchiveFailureReason.HashMismatch, error.Reason);
    }

    [Fact]
    public async Task OpenAsync_RollbackThroughAnotherFeedOverAForgedInstall_RefusesWithTheRecordedHash()
    {
        AssertExpectedFilesystem(_root);
        WritePackage("1.0.0");
        var feedDirectory = Path.Combine(_root, "feed");
        ActivePackage original;
        using (var host = CreateHost(feedDirectory, createFeedPackage: false))
        {
            await ReconcileAsync(host);
            original = await GetActivePackageAsync(host);
            var newer = WritePackage("2.0.0");
            await ReconcileAsync(host);
            Assert.Equal("2.0.0", (await GetActivePackageAsync(host)).Version);
            File.Delete(newer);
        }

        // 1.0.0 is recorded but no longer active. A forged copy waits where a second feed installs it.
        var mirrorInstall = PackageInstallStore.GetInstallDirectory(Path.Combine(_root, "packages"), "mirror", PackageId, "1.0.0");
        foreach (var file in Directory.EnumerateFiles(original.InstallPath, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(mirrorInstall, Path.GetRelativePath(original.InstallPath, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        var forged = BuildPackage("1.0.0", "export const addon = 'forged';");
        await File.WriteAllBytesAsync(PackageInstallStore.GetArchivePath(mirrorInstall), forged);
        await File.WriteAllTextAsync(Path.Combine(mirrorInstall, PackageInstallStore.ContentHashFileName),
            "sha512:" + Convert.ToBase64String(System.Security.Cryptography.SHA512.HashData(forged)));

        using var mirror = CreateHost(feedDirectory, createFeedPackage: false, feedName: "mirror");
        await ReconcileAsync(mirror);
        var rolledBack = await GetActivePackageAsync(mirror);

        Assert.Equal("1.0.0", rolledBack.Version);
        Assert.Equal(Path.GetFullPath(mirrorInstall), Path.GetFullPath(rolledBack.InstallPath));
        Assert.Equal(original.ArchiveHash, rolledBack.ArchiveHash);
        var error = await Assert.ThrowsAsync<PackageArchiveException>(() =>
            mirror.Services.GetRequiredService<IPackageArchiveReader>().OpenAsync(rolledBack).AsTask());
        Assert.Equal(PackageArchiveFailureReason.HashMismatch, error.Reason);
    }

    private static string PackageArchiveHash(ActivePackage package) =>
        "sha512:" + Convert.ToBase64String(System.Security.Cryptography.SHA512.HashData(
            File.ReadAllBytes(PackageInstallStore.GetArchivePath(package.InstallPath))));

    private string WritePackage(string version)
    {
        var feedDirectory = Path.Combine(_root, "feed");
        Directory.CreateDirectory(feedDirectory);
        var path = Path.Combine(feedDirectory, $"{PackageId}.{version}.nupkg");
        File.WriteAllBytes(path, BuildPackage(version, ModuleScript));
        return path;
    }

    private IHost CreateHost(
        string feedPackageOrDirectory,
        bool createFeedPackage = true,
        string? packageInstallRoot = null,
        string feedName = "addons")
    {
        var feedDirectory = createFeedPackage ? Path.GetDirectoryName(feedPackageOrDirectory)! : feedPackageOrDirectory;
        var configuration = new Dictionary<string, string?>
        {
            ["Nuplane:Reconciliation:EnableAutomaticReconciliation"] = "false",
            ["Nuplane:Reconciliation:MaxRetryAttempts"] = "0",
            ["Nuplane:FeedResolution:OfflineMode"] = "true",
            ["Nuplane:FeedResolution:PackageInstallRoot"] = packageInstallRoot ?? Path.Combine(_root, "packages"),
            ["Nuplane:StoreRegistry:StateFilePath"] = Path.Combine(_root, "store-state.json")
        };

        return new HostBuilder()
            .ConfigureAppConfiguration((_, builder) => builder.AddInMemoryCollection(configuration))
            .ConfigureLogging(logging => logging.ClearProviders().SetMinimumLevel(LogLevel.Warning))
            .ConfigureServices((context, services) => services.AddNuplane(context.Configuration.GetSection("Nuplane"),
                nuplane => nuplane.AddDirectoryFeed(feedName, feedDirectory, feed =>
                {
                    feed.Include(PackageId);
                    feed.Watch = false;
                })))
            .Build();
    }

    private static async Task ReconcileAsync(IHost host)
    {
        var result = await host.Services.GetRequiredService<IReconciliationService>()
            .TriggerAsync(ReconciliationTrigger.Manual(), CancellationToken.None);
        Assert.False(result.Skipped);
        Assert.Empty(result.FailedPackages);
    }

    private static async Task<ActivePackage> GetActivePackageAsync(IHost host)
    {
        var snapshot = await host.Services.GetRequiredService<IActivePackageCatalog>()
            .GetActivePackagesAsync(CancellationToken.None);
        return Assert.Single(snapshot.Packages);
    }

    private static byte[] BuildPackage(string version, string moduleScript)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, $"{PackageId}.nuspec", $"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>{PackageId}</id>
                    <version>{version}</version>
                    <authors>nuplane-tests</authors>
                    <description>Archive reader acceptance fixture.</description>
                  </metadata>
                </package>
                """);
            Write(archive, "staticwebassets/module.js", moduleScript);
        }

        return stream.ToArray();
    }

    private static void Write(ZipArchive archive, string path, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void AssertExpectedFilesystem(string path)
    {
        if (Environment.GetEnvironmentVariable("NUPLANE_EXPECT_FILESYSTEM") is not { Length: > 0 } expected)
            return;
        Assert.True(OperatingSystem.IsLinux(), "NUPLANE_EXPECT_FILESYSTEM is checked on Linux only.");

        // mountinfo: "id parent major:minor root mountpoint options [optional...] - fstype source superoptions"
        var full = Path.GetFullPath(path);
        string? actual = null;
        var longest = -1;
        foreach (var line in File.ReadLines("/proc/self/mountinfo"))
        {
            var fields = line.Split(' ');
            var separator = Array.IndexOf(fields, "-");
            if (fields.Length < 5 || separator < 0 || separator + 1 >= fields.Length)
                continue;
            var mountPoint = fields[4].Replace("\\040", " ");
            var contains = mountPoint == "/" || full == mountPoint || full.StartsWith(mountPoint + "/", StringComparison.Ordinal);
            // A later row at the same mount point covers an earlier one.
            if (contains && mountPoint.Length >= longest)
            {
                longest = mountPoint.Length;
                actual = fields[separator + 1];
            }
        }

        Assert.Equal(expected, actual);
    }
}
