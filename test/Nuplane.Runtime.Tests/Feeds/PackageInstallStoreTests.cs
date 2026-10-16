using System.IO.Compression;
using System.Security.Cryptography;
using Nuplane.Feeds;
using Nuplane.Runtime.Tests.TestSupport;

namespace Nuplane.Runtime.Tests.Feeds;

public sealed class PackageInstallStoreTests
{
    [Fact]
    public async Task OrdinaryProbeAndHashRead_IncidentalControlDataRetainsReleasedCompatibility()
    {
        using var install = new TempDirectory();
        var control = install.CreateSubdirectory(".nuplane-store");
        File.WriteAllText(Path.Combine(control, "membership.json"), "malformed incidental data");
        install.WriteFile(PackageInstallStore.CompletionMarkerFileName, "ordinary marker contents");
        var hash = "sha512:" + Convert.ToBase64String(new byte[64]);
        install.WriteFile(PackageInstallStore.ContentHashFileName, hash);

        Assert.True(PackageInstallStore.IsInstalled(install.Path));
        Assert.Equal(hash, await PackageInstallStore.ReadContentHashAsync(install.Path, CancellationToken.None));
        File.Delete(Path.Combine(install.Path, PackageInstallStore.CompletionMarkerFileName));
        Assert.False(PackageInstallStore.IsInstalled(install.Path));
        Assert.Equal(hash, await PackageInstallStore.ReadContentHashAsync(install.Path, CancellationToken.None));
        File.Delete(Path.Combine(install.Path, PackageInstallStore.ContentHashFileName));
        Assert.Null(await PackageInstallStore.ReadContentHashAsync(install.Path, CancellationToken.None));
    }

    [Theory]
    [InlineData("sha512:invalid")]
    [InlineData("sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("sha512:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB==")]
    public async Task ReadContentHashAsync_InvalidOrNonCanonicalHashReturnsNull(string hash)
    {
        using var install = new TempDirectory();
        install.WriteFile(PackageInstallStore.ContentHashFileName, hash);

        Assert.Null(await PackageInstallStore.ReadContentHashAsync(install.Path, CancellationToken.None));
    }

    [Fact]
    public async Task ReadContentHashAsync_CancelledReadPropagatesCancellation()
    {
        using var install = new TempDirectory();
        install.WriteFile(PackageInstallStore.ContentHashFileName, "sha512:" + Convert.ToBase64String(new byte[64]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PackageInstallStore.ReadContentHashAsync(install.Path, cancellation.Token));
    }

    [Fact]
    public async Task InstallAsync_ArchiveBytes_PersistsCanonicalSha512BeforePublishing()
    {
        using var packages = new TempDirectory();
        using var installRoot = new TempDirectory();
        var archivePath = NupkgTestBuilder.Create("Plugin", "1.0.0").BuildTo(packages.Path);
        var expectedHash = "sha512:" + Convert.ToBase64String(SHA512.HashData(await File.ReadAllBytesAsync(archivePath)));
        var installDirectory = Path.Combine(installRoot.Path, "installed");

        await PackageInstallStore.InstallAsync(installRoot.Path, installDirectory, archivePath, CancellationToken.None);

        Assert.True(PackageInstallStore.IsInstalled(installDirectory));
        Assert.Equal(expectedHash, await File.ReadAllTextAsync(Path.Combine(installDirectory, ".nuplane-content-hash")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(installRoot.Path, ".tmp")));
    }

    [Fact]
    public async Task InstallAsync_ArchiveSuppliesHashMetadata_OverwritesWithActualArchiveHash()
    {
        using var packages = new TempDirectory();
        using var installRoot = new TempDirectory();
        var archivePath = NupkgTestBuilder.Create("Plugin", "1.0.0").BuildTo(packages.Path);
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(archive.CreateEntry(".nuplane-content-hash").Open());
            await writer.WriteAsync("sha512:forged");
        }
        var expectedHash = "sha512:" + Convert.ToBase64String(SHA512.HashData(await File.ReadAllBytesAsync(archivePath)));
        var installDirectory = Path.Combine(installRoot.Path, "installed");

        await PackageInstallStore.InstallAsync(installRoot.Path, installDirectory, archivePath, CancellationToken.None);

        Assert.Equal(expectedHash, await File.ReadAllTextAsync(Path.Combine(installDirectory, ".nuplane-content-hash")));
    }
}
