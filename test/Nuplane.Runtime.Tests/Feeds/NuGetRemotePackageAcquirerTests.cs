using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Feeds;
using Nuplane.Feeds.Configuration;
using Nuplane.Runtime.Tests.TestSupport;

namespace Nuplane.Runtime.Tests.Feeds;

public sealed class NuGetRemotePackageAcquirerTests
{
    [Fact]
    public async Task AcquireAsync_RemoteArchive_PersistsExactDownloadedContentHash()
    {
        using var installRoot = new TempDirectory();
        var bytes = NupkgTestBuilder.Create("Plugin", "1.0.0").Build();
        await using var server = new TestNuGetFeedServer("Plugin", "1.0.0", bytes);
        var acquirer = new NuGetRemotePackageAcquirer(Options.Create(new FeedResolutionOptions { PackageInstallRoot = installRoot.Path }));
        var feed = new FeedDefinition("remote", server.ServiceIndexUri);

        var installDirectory = await acquirer.AcquireAsync(feed, "Plugin", "1.0.0", CancellationToken.None);
        var cachedDirectory = await acquirer.AcquireAsync(feed, "Plugin", "1.0.0", CancellationToken.None);

        Assert.Equal("sha512:" + Convert.ToBase64String(SHA512.HashData(bytes)),
            await File.ReadAllTextAsync(Path.Combine(installDirectory, ".nuplane-content-hash")));
        Assert.Equal(installDirectory, cachedDirectory);
        Assert.Equal(1, server.PackageDownloads);
    }
}
