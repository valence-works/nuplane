using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Feeds;
using Nuplane.PackageArchives;

namespace Nuplane.Runtime.Tests;

public sealed class PackageArchiveReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nuplane-archive-reader-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task OpenAsync_VerifiedArchive_ReadsEntriesCaseInsensitivelyWithEitherSeparator()
    {
        var archive = CreateArchive(("staticwebassets/js/module.js", "export {}"u8.ToArray()), ("Acme.nuspec", "<package/>"u8.ToArray()));
        var package = Install("Acme.Addon", "1.0.0", archive);

        var opened = await CreateReader().OpenAsync(package);

        Assert.Equal(Hash(archive), opened.ArchiveHash);
        Assert.Equal("export {}"u8.ToArray(), opened.GetEntry("staticwebassets/js/module.js")!.ReadAllBytes());
        Assert.Equal("export {}"u8.ToArray(), opened.GetEntry(@"StaticWebAssets\JS\Module.js")!.ReadAllBytes());
        Assert.Equal(9, opened.GetEntry("staticwebassets/js/module.js")!.Length);
        Assert.Equal(["staticwebassets/js/module.js", "Acme.nuspec"], opened.Entries.Select(static entry => entry.Path));
        Assert.Null(opened.GetEntry("staticwebassets/js/missing.js"));
    }

    [Fact]
    public async Task OpenAsync_ModifiedArchive_RefusesWithHashMismatchNamingThePackage()
    {
        var archive = CreateArchive(("content/a.txt", "original"u8.ToArray()));
        var package = Install("Acme.Addon", "1.0.0", archive);
        var path = ArchivePath(package);
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[^10] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);

        var error = await Assert.ThrowsAsync<PackageArchiveException>(() => CreateReader().OpenAsync(package).AsTask());

        Assert.Equal(PackageArchiveFailureReason.HashMismatch, error.Reason);
        Assert.Equal("Acme.Addon", error.PackageId);
        Assert.Equal("1.0.0", error.Version);
        Assert.Contains("'Acme.Addon' 1.0.0", error.Message, StringComparison.Ordinal);
        Assert.Contains("HashMismatch", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_ReplacedArchive_RefusesWithHashMismatch()
    {
        var package = Install("Acme.Addon", "1.0.0", CreateArchive(("content/a.txt", "original"u8.ToArray())));
        await File.WriteAllBytesAsync(ArchivePath(package), CreateArchive(("content/a.txt", "replaced"u8.ToArray())));

        var error = await Assert.ThrowsAsync<PackageArchiveException>(() => CreateReader().OpenAsync(package).AsTask());

        Assert.Equal(PackageArchiveFailureReason.HashMismatch, error.Reason);
    }

    [Fact]
    public async Task OpenAsync_MissingArchive_RefusesWithArchiveMissing()
    {
        var package = Install("Acme.Addon", "1.0.0", CreateArchive(("content/a.txt", "a"u8.ToArray())));
        File.Delete(ArchivePath(package));

        var error = await Assert.ThrowsAsync<PackageArchiveException>(() => CreateReader().OpenAsync(package).AsTask());

        Assert.Equal(PackageArchiveFailureReason.ArchiveMissing, error.Reason);
    }

    [Fact]
    public async Task OpenAsync_ArchivePathIsNotAReadableFile_RefusesWithArchiveUnreadable()
    {
        var package = Install("Acme.Addon", "1.0.0", CreateArchive(("content/a.txt", "a"u8.ToArray())));
        File.Delete(ArchivePath(package));
        Directory.CreateDirectory(ArchivePath(package));

        var error = await Assert.ThrowsAsync<PackageArchiveException>(() => CreateReader().OpenAsync(package).AsTask());

        Assert.Equal(PackageArchiveFailureReason.ArchiveUnreadable, error.Reason);
        Assert.Equal("Acme.Addon", error.PackageId);
    }

    [Theory]
    [InlineData("fifo")]
    [InlineData("link-to-fifo")]
    [InlineData("link-to-file")]
    public async Task OpenAsync_ArchiveReplacedByFifoOrLink_RefusesWithoutBlocking(string replacement)
    {
        // Named pipes are a Unix filesystem entry; the link rows also run on Windows where links can be created.
        if (OperatingSystem.IsWindows() && replacement != "link-to-file")
            return;
        var archive = CreateArchive(("content/a.txt", "a"u8.ToArray()));
        var package = Install("Acme.Addon", "1.0.0", archive);
        File.Delete(ArchivePath(package));
        var target = Path.Combine(package.InstallPath, "target");
        if (replacement == "link-to-file")
        {
            // Even a link to the genuine archive bytes is refused: a kept archive is a regular file.
            await File.WriteAllBytesAsync(target, archive);
        }
        else
        {
            using var mkfifo = System.Diagnostics.Process.Start("mkfifo",
                replacement == "fifo" ? ArchivePath(package) : target);
            await mkfifo.WaitForExitAsync();
        }

        if (replacement != "fifo")
        {
            try
            {
                File.CreateSymbolicLink(ArchivePath(package), target);
            }
            catch (IOException) when (OperatingSystem.IsWindows())
            {
                return; // Creating links needs Developer Mode or an elevated process on Windows.
            }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
            {
                return;
            }
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var error = await Assert.ThrowsAsync<PackageArchiveException>(() =>
            CreateReader().OpenAsync(package, timeout.Token).AsTask());

        Assert.Equal(PackageArchiveFailureReason.ArchiveUnreadable, error.Reason);
    }

    [Fact]
    public async Task OpenAsync_RecentlyRefusedArchive_RefusesAgainWithoutRereading()
    {
        var archive = CreateArchive(("content/a.txt", "a"u8.ToArray()));
        var package = Install("Acme.Addon", "1.0.0", archive);
        File.Delete(ArchivePath(package));
        var reader = CreateReader();
        var first = await Assert.ThrowsAsync<PackageArchiveException>(() => reader.OpenAsync(package).AsTask());
        await File.WriteAllBytesAsync(ArchivePath(package), archive);

        var second = await Assert.ThrowsAsync<PackageArchiveException>(() => reader.OpenAsync(package).AsTask());

        Assert.Equal(PackageArchiveFailureReason.ArchiveMissing, first.Reason);
        Assert.Equal(first.Reason, second.Reason);
        Assert.Equal(first.Message, second.Message);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task OpenAsync_ArchiveOpenThatNeverReturns_RefusesWhenTheOpenBoundExpires()
    {
        var package = Install("Acme.Addon", "1.0.0", CreateArchive(("content/a.txt", "a"u8.ToArray())));
        using var neverOpens = new ManualResetEventSlim();
        var reader = new PackageArchiveReader(Options.Create(new PackageArchiveReaderOptions()),
            NullLogger<PackageArchiveReader>.Instance, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(200),
            _ => { neverOpens.Wait(); throw new IOException("released"); });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            var error = await Assert.ThrowsAsync<PackageArchiveException>(() =>
                reader.OpenAsync(package, timeout.Token).AsTask());

            Assert.Equal(PackageArchiveFailureReason.ArchiveUnreadable, error.Reason);
            Assert.IsType<TimeoutException>(error.InnerException);
        }
        finally
        {
            neverOpens.Set();
        }
    }

    [Fact]
    public async Task OpenAsync_RefusalAfterItsRetention_ReadsTheRepairedArchive()
    {
        var archive = CreateArchive(("content/a.txt", "repaired"u8.ToArray()));
        var package = Install("Acme.Addon", "1.0.0", archive);
        File.Delete(ArchivePath(package));
        var reader = new PackageArchiveReader(Options.Create(new PackageArchiveReaderOptions()),
            NullLogger<PackageArchiveReader>.Instance, TimeSpan.Zero, TimeSpan.FromSeconds(30), openArchive: null);
        _ = await Assert.ThrowsAsync<PackageArchiveException>(() => reader.OpenAsync(package).AsTask());
        await File.WriteAllBytesAsync(ArchivePath(package), archive);

        var opened = await reader.OpenAsync(package);

        Assert.Equal("repaired"u8.ToArray(), opened.GetEntry("content/a.txt")!.ReadAllBytes());
    }

    [Fact]
    public async Task OpenAsync_ConcurrentColdOpens_ShareOneVerifiedArchive()
    {
        var package = Install("Acme.Addon", "1.0.0",
            CreateArchive(("content/a.bin", RandomNumberGenerator.GetBytes(4 * 1024 * 1024))));
        var reader = CreateReader();

        var opened = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => reader.OpenAsync(package).AsTask())));

        Assert.All(opened, archive => Assert.Same(opened[0], archive));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sha512:not-base64")]
    [InlineData("sha256:AAAA")]
    public async Task OpenAsync_WithoutCanonicalRecordedHash_RefusesWithHashNotRecorded(string? recordedHash)
    {
        var package = Install("Acme.Addon", "1.0.0", CreateArchive(("content/a.txt", "a"u8.ToArray()))) with
        {
            ArchiveHash = recordedHash
        };

        var error = await Assert.ThrowsAsync<PackageArchiveException>(() => CreateReader().OpenAsync(package).AsTask());

        Assert.Equal(PackageArchiveFailureReason.HashNotRecorded, error.Reason);
    }

    [Fact]
    public async Task OpenAsync_ArchiveOverLimit_RefusesWithArchiveTooLarge()
    {
        var archive = CreateArchive(("content/a.txt", new byte[4096]));
        var package = Install("Acme.Addon", "1.0.0", archive);

        var error = await Assert.ThrowsAsync<PackageArchiveException>(() =>
            CreateReader(new PackageArchiveReaderOptions { MaximumArchiveBytes = archive.Length - 1 }).OpenAsync(package).AsTask());

        Assert.Equal(PackageArchiveFailureReason.ArchiveTooLarge, error.Reason);
    }

    [Fact]
    public async Task OpenAsync_HashMatchesButNotAZip_RefusesWithInvalidArchive()
    {
        var package = Install("Acme.Addon", "1.0.0", "not a zip archive"u8.ToArray());

        var error = await Assert.ThrowsAsync<PackageArchiveException>(() => CreateReader().OpenAsync(package).AsTask());

        Assert.Equal(PackageArchiveFailureReason.InvalidArchive, error.Reason);
    }

    [Fact]
    public async Task OpenAsync_CachedArchive_ServesTheVerifiedCopyWithoutRereading()
    {
        var package = Install("Acme.Addon", "1.0.0", CreateArchive(("content/a.txt", "verified"u8.ToArray())));
        var reader = CreateReader();
        var first = await reader.OpenAsync(package);
        File.Delete(ArchivePath(package));

        var second = await reader.OpenAsync(package);

        Assert.Same(first, second);
        Assert.Equal("verified"u8.ToArray(), second.GetEntry("content/a.txt")!.ReadAllBytes());
    }

    [Fact]
    public async Task OpenAsync_CacheDisabled_VerifiesEveryOpen()
    {
        var package = Install("Acme.Addon", "1.0.0", CreateArchive(("content/a.txt", "verified"u8.ToArray())));
        var reader = CreateReader(new PackageArchiveReaderOptions { CacheBytes = 0 });
        _ = await reader.OpenAsync(package);
        await File.WriteAllBytesAsync(ArchivePath(package), CreateArchive(("content/a.txt", "tampered"u8.ToArray())));

        var error = await Assert.ThrowsAsync<PackageArchiveException>(() => reader.OpenAsync(package).AsTask());

        Assert.Equal(PackageArchiveFailureReason.HashMismatch, error.Reason);
    }

    [Fact]
    public async Task OpenAsync_CacheOverBudget_EvictsTheLeastRecentlyUsedArchive()
    {
        var archives = Enumerable.Range(0, 3)
            .Select(index => CreateArchive(($"content/{index}.txt", RandomNumberGenerator.GetBytes(2048))))
            .ToArray();
        var packages = archives.Select((archive, index) => Install($"Acme.Package{index}", "1.0.0", archive)).ToArray();
        var reader = CreateReader(new PackageArchiveReaderOptions
        {
            CacheBytes = archives[0].Length + archives[1].Length + archives[2].Length - 1
        });
        var first = await reader.OpenAsync(packages[0]);
        _ = await reader.OpenAsync(packages[1]);
        Assert.Same(first, await reader.OpenAsync(packages[0]));   // touch the first: the second is now oldest
        _ = await reader.OpenAsync(packages[2]);                   // over budget: evict one
        File.Delete(ArchivePath(packages[0]));
        File.Delete(ArchivePath(packages[1]));

        Assert.Same(first, await reader.OpenAsync(packages[0]));
        var error = await Assert.ThrowsAsync<PackageArchiveException>(() => reader.OpenAsync(packages[1]).AsTask());
        Assert.Equal(PackageArchiveFailureReason.ArchiveMissing, error.Reason);
    }

    private PackageArchiveReader CreateReader(PackageArchiveReaderOptions? options = null) =>
        new(Options.Create(options ?? new PackageArchiveReaderOptions()), NullLogger<PackageArchiveReader>.Instance);

    private ActivePackage Install(string packageId, string version, byte[] archive) =>
        InstallAt(_root, packageId, version, archive);

    internal static ActivePackage InstallAt(string root, string packageId, string version, byte[] archive)
    {
        var installPath = Path.Combine(root, "feed", packageId, version);
        Directory.CreateDirectory(installPath);
        File.WriteAllBytes(PackageInstallStore.GetArchivePath(installPath), archive);
        return new ActivePackage(packageId, version, "feed", "source", installPath, DateTimeOffset.UtcNow, "correlation")
        {
            ArchiveHash = Hash(archive)
        };
    }

    private static string ArchivePath(ActivePackage package) => PackageInstallStore.GetArchivePath(package.InstallPath);

    internal static string Hash(byte[] archive) => "sha512:" + Convert.ToBase64String(SHA512.HashData(archive));

    internal static byte[] CreateArchive(params (string Path, byte[] Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                var entry = archive.CreateEntry(path);
                if (path.EndsWith('/'))
                    continue;
                using var output = entry.Open();
                output.Write(content);
            }
        }

        return stream.ToArray();
    }
}
