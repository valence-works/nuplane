using System.IO.Compression;
using System.Text;
using Xunit;

namespace Nuplane.Runtime.Tests;

public sealed class PackageContentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nuplane-package-content-{Guid.NewGuid():N}");

    public PackageContentTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void TryReadFile_ReadsRootFileFromExtractedDirectory()
    {
        File.WriteAllText(Path.Combine(_root, "manifest.json"), "{\"ok\":true}");

        var bytes = PackageContent.TryReadFile(_root, "manifest.json");

        Assert.Equal("{\"ok\":true}", AsText(bytes));
    }

    [Fact]
    public void TryReadFile_ReadsNestedFileUsingForwardOrBackSlashes()
    {
        Directory.CreateDirectory(Path.Combine(_root, "build"));
        File.WriteAllText(Path.Combine(_root, "build", "manifest.json"), "nested");

        Assert.Equal("nested", AsText(PackageContent.TryReadFile(_root, "build/manifest.json")));
        Assert.Equal("nested", AsText(PackageContent.TryReadFile(_root, "build\\manifest.json")));
        Assert.Equal("nested", AsText(PackageContent.TryReadFile(_root, "build/./manifest.json")));
        Assert.Equal("nested", AsText(PackageContent.TryReadFile(_root, "build//manifest.json")));
    }

    [Fact]
    public void TryReadFile_ReturnsNullForMissingFileOrPath()
    {
        Assert.Null(PackageContent.TryReadFile(_root, "absent.json"));
        Assert.Null(PackageContent.TryReadFile(Path.Combine(_root, "does-not-exist"), "manifest.json"));
        Assert.Null(PackageContent.TryReadFile("", "manifest.json"));
    }

    [Fact]
    public void TryReadFile_ReadsEntryFromNupkgArchive()
    {
        var nupkg = CreateNupkg(("manifest.json", "from-archive"), ("build/manifest.json", "nested-archive"));

        Assert.Equal("from-archive", AsText(PackageContent.TryReadFile(nupkg, "manifest.json")));
        Assert.Equal("nested-archive", AsText(PackageContent.TryReadFile(nupkg, "build/manifest.json")));
        Assert.Null(PackageContent.TryReadFile(nupkg, "missing.json"));
    }

    [Fact]
    public void TryReadFile_PreservesFirstCaseInsensitiveArchiveMatch()
    {
        var nupkg = CreateNupkg(("manifest.json", "first"), ("MANIFEST.JSON", "second"));

        Assert.Equal("first", AsText(PackageContent.TryReadFile(nupkg, "manifest.json")));
        Assert.Equal("first", AsText(PackageContent.TryReadFile(nupkg, "Manifest.Json")));
    }

    [Fact]
    public void TryReadFile_PreservesReleasedArchiveNormalizationAndRequestValidation()
    {
        var nupkg = CreateNupkg(("../manifest.json", "traversal"), ("/manifest.json", "rooted"), ("C:/manifest.json", "drive"));

        Assert.Equal("rooted", AsText(PackageContent.TryReadFile(nupkg, "manifest.json")));
        Assert.Null(PackageContent.TryReadFile(nupkg, "../manifest.json"));
        Assert.Equal(Path.IsPathRooted("C:/manifest.json") ? null : "drive",
            AsText(PackageContent.TryReadFile(nupkg, "C:/manifest.json")));
    }

    [Fact]
    public void TryReadFile_RejectsPathTraversalAndAbsolutePaths()
    {
        // A secret living next to (outside) the package root must never be reachable via traversal.
        var secret = Path.Combine(_root, "outside-secret.txt");
        File.WriteAllText(secret, "top-secret");
        var packageDir = Path.Combine(_root, "package");
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "manifest.json"), "ok");

        Assert.Equal("ok", AsText(PackageContent.TryReadFile(packageDir, "manifest.json")));
        Assert.Null(PackageContent.TryReadFile(packageDir, "../outside-secret.txt"));
        Assert.Null(PackageContent.TryReadFile(packageDir, "..\\outside-secret.txt"));
        Assert.Null(PackageContent.TryReadFile(packageDir, secret));
    }

    [Fact]
    public void TryFindByExtension_FindsRootFileInDirectory()
    {
        File.WriteAllText(Path.Combine(_root, "Acme.Package.nuspec"), "<package/>");
        Directory.CreateDirectory(Path.Combine(_root, "lib"));
        File.WriteAllText(Path.Combine(_root, "lib", "ignored.nuspec"), "should not match (not root)");

        var found = PackageContent.TryFindByExtension(_root, ".nuspec");

        Assert.NotNull(found);
        Assert.Equal("Acme.Package.nuspec", found!.Name);
        Assert.Equal("<package/>", Encoding.UTF8.GetString(found.Content));
    }

    [Fact]
    public void TryFindByExtension_FindsRootEntryInNupkgArchive()
    {
        var nupkg = CreateNupkg(("Acme.Package.nuspec", "<archive-nuspec/>"), ("lib/net10.0/Acme.dll", "binary"));

        var found = PackageContent.TryFindByExtension(nupkg, ".nuspec");

        Assert.NotNull(found);
        Assert.Equal("Acme.Package.nuspec", found!.Name);
        Assert.Equal("<archive-nuspec/>", Encoding.UTF8.GetString(found.Content));
    }

    [Fact]
    public void TryFindByExtension_PreservesFirstRootArchiveMatch()
    {
        var nupkg = CreateNupkg(("Acme.Package.nuspec", "first"), ("ACME.PACKAGE.NUSPEC", "second"));

        var found = Assert.IsType<PackageContentFile>(PackageContent.TryFindByExtension(nupkg, ".nuspec"));
        Assert.Equal("Acme.Package.nuspec", found.Name);
        Assert.Equal("first", AsText(found.Content));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryReadersRemainAvailableBesideUnverifiableCoordinationMetadata(bool archive)
    {
        // Ordinary callers neither join the protocol nor claim that this is an unenrolled store.
        var control = Directory.CreateDirectory(Path.Combine(_root, ".nuplane-store"));
        File.WriteAllText(Path.Combine(control.FullName, "membership.json"), "unverifiable metadata");
        var installPath = archive ? CreateNupkg(("manifest.json", "ordinary"), ("Package.nuspec", "nuspec")) : _root;
        if (!archive)
        {
            File.WriteAllText(Path.Combine(_root, "manifest.json"), "ordinary");
            File.WriteAllText(Path.Combine(_root, "Package.nuspec"), "nuspec");
        }

        Assert.Equal("ordinary", AsText(PackageContent.TryReadFile(installPath, "manifest.json")));
        var found = Assert.IsType<PackageContentFile>(PackageContent.TryFindByExtension(installPath, ".nuspec"));
        Assert.Equal("Package.nuspec", found.Name);
        Assert.Equal("nuspec", AsText(found.Content));
    }

    private string CreateNupkg(params (string Path, string Content)[] entries)
    {
        var nupkgPath = Path.Combine(_root, $"package-{Guid.NewGuid():N}.nupkg");
        using var stream = File.Create(nupkgPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (path, content) in entries)
        {
            var entry = archive.CreateEntry(path);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return nupkgPath;
    }

    private static string? AsText(byte[]? bytes) => bytes is null ? null : Encoding.UTF8.GetString(bytes);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
