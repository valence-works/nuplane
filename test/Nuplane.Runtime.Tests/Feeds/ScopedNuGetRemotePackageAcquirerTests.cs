using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Feeds.Configuration;
using Nuplane.Feeds.Credentials;
using Nuplane.Runtime.Tests.TestSupport;

namespace Nuplane.Runtime.Tests.Feeds;

public sealed class ScopedNuGetRemotePackageAcquirerTests
{
    [Fact]
    public async Task AcquireAsync_NativeInstallAndCache_PreserveExactArchiveHashAndPerRequestAuthentication()
    {
        using var fixture = await EnrolledEmptyPackageStoreFixture.CreateAsync();
        var bytes = NupkgTestBuilder.Create("Plugin", "1.0.0").Build();
        const string secret = "scoped-native-feed-test-token";
        var variableName = "NUPLANE_TEST_NATIVE_FEED_" + Guid.NewGuid().ToString("N");
        using var variable = new EnvironmentVariableScope(variableName, secret);
        await using var server = new TestNuGetFeedServer("Plugin", "1.0.0", bytes,
            requiredAuth: new TestFeedBasicAuth(FeedCredential.TokenUserName, secret));
        var acquirer = new NuGetRemotePackageAcquirer(
            Options.Create(new FeedResolutionOptions { PackageInstallRoot = fixture.InstallRoot }),
            new SecretReferenceResolver([new EnvironmentSecretReferenceProvider()]));
        var feed = new FeedDefinition("remote", server.ServiceIndexUri, "secrets://env/" + variableName);
        await using var operation = await fixture.Admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Reconciliation);
        using var borrow = operation.Owner!.Borrow();

        var installed = await acquirer.AcquireAsync(feed, "Plugin", "1.0.0", borrow, CancellationToken.None);
        var cached = await acquirer.AcquireAsync(feed, "Plugin", "1.0.0", borrow, CancellationToken.None);

        Assert.Equal(PackageInstallStore.GetInstallDirectory(fixture.InstallRoot, feed.Name, "Plugin", "1.0.0"), installed);
        Assert.Equal(installed, cached);
        Assert.True(PackageInstallStore.IsInstalled(installed, borrow));
        Assert.Equal("sha512:" + Convert.ToBase64String(SHA512.HashData(bytes)),
            await PackageInstallStore.ReadContentHashAsync(installed, borrow, CancellationToken.None));
        Assert.Equal(1, server.PackageDownloads);
        Assert.Equal(2, server.AuthorizedRequests);
        Assert.Equal(0, server.UnauthorizedRequests);
    }

    [Fact]
    public async Task AcquireAsync_RefusedCredentials_RejectCachedNativeInstallBeforeNetwork()
    {
        using var fixture = await EnrolledEmptyPackageStoreFixture.CreateAsync();
        await using var server = new TestNuGetFeedServer("Plugin", "1.0.0", NupkgTestBuilder.Create("Plugin", "1.0.0").Build());
        var acquirer = new NuGetRemotePackageAcquirer(Options.Create(new FeedResolutionOptions { PackageInstallRoot = fixture.InstallRoot }));
        var feed = new FeedDefinition("remote", server.ServiceIndexUri);
        await using var operation = await fixture.Admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Reconciliation);
        using var borrow = operation.Owner!.Borrow();
        var installed = await acquirer.AcquireAsync(feed, "Plugin", "1.0.0", borrow, CancellationToken.None);
        var refusedFeed = new FeedDefinition(feed.Name, feed.ServiceIndex, "secrets://missing-provider/scoped-token");

        var refusal = await Assert.ThrowsAsync<FeedCredentialUnavailableException>(() =>
            acquirer.AcquireAsync(refusedFeed, "Plugin", "1.0.0", borrow, CancellationToken.None));

        Assert.Equal(feed.Name, refusal.FeedName);
        Assert.True(PackageInstallStore.IsInstalled(installed, borrow));
        Assert.Equal(2, server.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquireAsync_WrongRootOrExpiredBorrow_RefusesBeforeNetwork(bool expired)
    {
        using var fixture = await EnrolledEmptyPackageStoreFixture.CreateAsync();
        using var otherRoot = new TempDirectory();
        await using var server = new TestNuGetFeedServer("Plugin", "1.0.0", NupkgTestBuilder.Create("Plugin", "1.0.0").Build());
        var acquirer = new NuGetRemotePackageAcquirer(Options.Create(new FeedResolutionOptions
        {
            PackageInstallRoot = expired ? fixture.InstallRoot : otherRoot.Path
        }));
        await using var operation = await fixture.Admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Reconciliation);
        using var borrow = operation.Owner!.Borrow();
        if (expired)
            borrow.Dispose();

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            acquirer.AcquireAsync(new FeedDefinition("remote", server.ServiceIndexUri), "Plugin", "1.0.0", borrow, CancellationToken.None));

        Assert.Equal(0, server.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(otherRoot.Path));
    }
}
