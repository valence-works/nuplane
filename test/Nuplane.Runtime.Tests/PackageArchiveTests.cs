using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nuplane.PackageArchives;

namespace Nuplane.Runtime.Tests;

public sealed class PackageArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nuplane-archive-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ReadAllBytes_ConcurrentReads_ReturnEachEntryExactly()
    {
        var files = Enumerable.Range(0, 32)
            .Select(index => ($"staticwebassets/file{index}.js", RandomNumberGenerator.GetBytes(4096 + index)))
            .ToArray();
        var opened = await CreateReader().OpenAsync(PackageArchiveReaderTests.InstallAt(_root, "Acme.Addon", "1.0.0", PackageArchiveReaderTests.CreateArchive(files)));

        var reads = await Task.WhenAll(Enumerable.Range(0, 256).Select(index => Task.Run(() =>
        {
            var (path, content) = files[index % files.Length];
            return (Expected: content, Actual: opened.GetEntry(path)!.ReadAllBytes());
        })));

        Assert.All(reads, read => Assert.Equal(read.Expected, read.Actual));
    }

    [Fact]
    public async Task GetEntry_EscapingInvalidOrAmbiguousNames_AreNotExposed()
    {
        var archive = PackageArchiveReaderTests.CreateArchive(
            ("../escaped.txt", "x"u8.ToArray()),
            ("content/./dot.txt", "x"u8.ToArray()),
            ("content//empty.txt", "x"u8.ToArray()),
            ("C:/rooted.txt", "x"u8.ToArray()),
            ("/leading.txt", "x"u8.ToArray()),
            ("content/dup.txt", "first"u8.ToArray()),
            ("content/DUP.txt", "second"u8.ToArray()),
            ("content/dir/", []),
            ("content/nul\0.txt", "x"u8.ToArray()),
            ("content/ok.txt", "ok"u8.ToArray()));
        var opened = await CreateReader().OpenAsync(PackageArchiveReaderTests.InstallAt(_root, "Acme.Addon", "1.0.0", archive));

        Assert.Equal(["content/ok.txt"], opened.Entries.Select(static entry => entry.Path));
        Assert.Null(opened.GetEntry("content/dup.txt"));
        Assert.Null(opened.GetEntry("content/dir"));
        Assert.Null(opened.GetEntry("content/../content/ok.txt"));
        Assert.Null(opened.GetEntry("/content/ok.txt"));
        Assert.Null(opened.GetEntry("content/ok.txt\0"));
        Assert.Null(opened.GetEntry(""));
    }

    [Fact]
    public async Task GetEntry_BackslashInEntryName_IsReadAsASeparator()
    {
        var archive = PackageArchiveReaderTests.CreateArchive(
            ("content\\nested\\a.txt", "a"u8.ToArray()),
            ("content/dup.txt", "first"u8.ToArray()),
            ("content\\dup.txt", "second"u8.ToArray()));
        var opened = await CreateReader().OpenAsync(PackageArchiveReaderTests.InstallAt(_root, "Acme.Addon", "1.0.0", archive));

        Assert.Equal("a"u8.ToArray(), opened.GetEntry("content/nested/a.txt")!.ReadAllBytes());
        Assert.Equal("content/nested/a.txt", Assert.Single(opened.Entries).Path);
        Assert.Null(opened.GetEntry("content/dup.txt"));
    }

    private static PackageArchiveReader CreateReader() =>
        new(Options.Create(new PackageArchiveReaderOptions()), NullLogger<PackageArchiveReader>.Instance);
}
