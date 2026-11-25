using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Feeds;

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
    public async Task GetEntry_EscapingInvalidOrAmbiguousNames_AreNotExposed()
    {
        var archive = CreateArchive(
            ("../escaped.txt", "x"u8.ToArray()),
            ("content/./dot.txt", "x"u8.ToArray()),
            ("content//empty.txt", "x"u8.ToArray()),
            ("C:/rooted.txt", "x"u8.ToArray()),
            ("/leading.txt", "x"u8.ToArray()),
            ("content/dup.txt", "first"u8.ToArray()),
            ("content/DUP.txt", "second"u8.ToArray()),
            ("content/dir/", []),
            ("content/ok.txt", "ok"u8.ToArray()));
        var opened = await CreateReader().OpenAsync(Install("Acme.Addon", "1.0.0", archive));

        Assert.Equal(["content/ok.txt"], opened.Entries.Select(static entry => entry.Path));
        Assert.Null(opened.GetEntry("content/dup.txt"));
        Assert.Null(opened.GetEntry("content/dir"));
        Assert.Null(opened.GetEntry("content/../content/ok.txt"));
        Assert.Null(opened.GetEntry("/content/ok.txt"));
        Assert.Null(opened.GetEntry("content/ok.txt\0"));
        Assert.Null(opened.GetEntry(""));
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
        var firstArchive = CreateArchive(("content/a.txt", RandomNumberGenerator.GetBytes(2048)));
        var secondArchive = CreateArchive(("content/b.txt", RandomNumberGenerator.GetBytes(2048)));
        var first = Install("Acme.First", "1.0.0", firstArchive);
        var second = Install("Acme.Second", "1.0.0", secondArchive);
        var reader = CreateReader(new PackageArchiveReaderOptions
        {
            CacheBytes = Math.Max(firstArchive.Length, secondArchive.Length) + 1
        });
        _ = await reader.OpenAsync(first);
        _ = await reader.OpenAsync(second);
        File.Delete(ArchivePath(first));

        var error = await Assert.ThrowsAsync<PackageArchiveException>(() => reader.OpenAsync(first).AsTask());

        Assert.Equal(PackageArchiveFailureReason.ArchiveMissing, error.Reason);
    }

    [Fact]
    public void Validate_RejectsOutOfRangeBounds()
    {
        var validator = new PackageArchiveReaderOptionsValidator();

        Assert.True(validator.Validate(null, new PackageArchiveReaderOptions()).Succeeded);
        Assert.True(validator.Validate(null, new PackageArchiveReaderOptions { MaximumArchiveBytes = 0 }).Failed);
        Assert.True(validator.Validate(null, new PackageArchiveReaderOptions { CacheBytes = -1 }).Failed);
    }

    private PackageArchiveReader CreateReader(PackageArchiveReaderOptions? options = null) =>
        new(Options.Create(options ?? new PackageArchiveReaderOptions()), NullLogger<PackageArchiveReader>.Instance);

    private ActivePackage Install(string packageId, string version, byte[] archive)
    {
        var installPath = Path.Combine(_root, "feed", packageId, version);
        Directory.CreateDirectory(installPath);
        File.WriteAllBytes(PackageInstallStore.GetArchivePath(installPath), archive);
        return new ActivePackage(packageId, version, "feed", "source", installPath, DateTimeOffset.UtcNow, "correlation")
        {
            ArchiveHash = Hash(archive)
        };
    }

    private static string ArchivePath(ActivePackage package) => PackageInstallStore.GetArchivePath(package.InstallPath);

    private static string Hash(byte[] archive) => "sha512:" + Convert.ToBase64String(SHA512.HashData(archive));

    private static byte[] CreateArchive(params (string Path, byte[] Content)[] files)
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
