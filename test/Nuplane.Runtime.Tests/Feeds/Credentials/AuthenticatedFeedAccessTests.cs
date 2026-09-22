using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Feeds;
using Nuplane.Feeds.Configuration;
using Nuplane.Feeds.Credentials;
using Nuplane.Feeds.Versioning;
using Nuplane.Runtime.Tests.TestSupport;

namespace Nuplane.Runtime.Tests.Feeds.Credentials;

/// <summary>
/// Both paths that talk to a feed — the package download and the version enumeration NuGet's own
/// client performs — against an in-process feed that answers nothing without basic authentication.
/// </summary>
public sealed class AuthenticatedFeedAccessTests : IDisposable
{
    private const string PackageId = "MyPlugin";
    private const string Version = "1.0.0";
    private const string FeedName = "private-feed";
    private const string Sentinel = "sentinel-feed-token-b83c17";

    private readonly TempDirectory _temp = new();
    private readonly byte[] _packageBytes = NupkgTestBuilder.Create(PackageId, Version).Build();
    private readonly string _variableName = "NUPLANE_TEST_FEED_TOKEN_" + Guid.NewGuid().ToString("N");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task AcquireAsync_WithAResolvableReference_SendsBasicAuthAndInstallsThePackage()
    {
        // A bare token travels as the password under the fixed placeholder user name.
        await using var server = StartServer(new(FeedCredential.TokenUserName, Sentinel));
        using var variable = new EnvironmentVariableScope(_variableName, Sentinel);

        var installPath = await CreateAcquirer().AcquireAsync(Feed(server), PackageId, Version, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(installPath, $"{PackageId}.nuspec")));
        Assert.Equal(1, server.PackageDownloads);
        // The service index and the package download, both authenticated, and nothing rejected:
        // the header is sent with the request rather than after a challenge.
        Assert.Equal(2, server.AuthorizedRequests);
        Assert.Equal(0, server.UnauthorizedRequests);
    }

    [Fact]
    public async Task AcquireAsync_WithAUserPasswordSecret_SendsBothHalves()
    {
        await using var server = StartServer(new("deploy", Sentinel));
        using var variable = new EnvironmentVariableScope(_variableName, $"deploy:{Sentinel}");

        var installPath = await CreateAcquirer().AcquireAsync(Feed(server), PackageId, Version, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(installPath, $"{PackageId}.nuspec")));
        Assert.Equal(0, server.UnauthorizedRequests);
    }

    [Fact]
    public async Task AcquireAsync_WithAnUnresolvableReference_RefusesTheFeedByNameWithoutContactingIt()
    {
        await using var server = StartServer(new(FeedCredential.TokenUserName, Sentinel));

        var exception = await Assert.ThrowsAsync<FeedCredentialUnavailableException>(
            () => CreateAcquirer().AcquireAsync(Feed(server), PackageId, Version, CancellationToken.None));

        Assert.Equal(FeedName, exception.FeedName);
        Assert.Contains(FeedName, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task AcquireAsync_WhenTheFeedIsRefused_DoesNotServeAPackageAnEarlierRunInstalled()
    {
        // The dangerous direction: with the install-store check first, a refused feed would keep
        // handing out what it fetched while it was still authorized, and the refusal would vanish
        // from the run instead of being reported.
        await using var server = StartServer(new(FeedCredential.TokenUserName, Sentinel));
        var acquirer = CreateAcquirer();
        var feed = Feed(server);
        string installPath;
        using (var variable = new EnvironmentVariableScope(_variableName, Sentinel))
        {
            installPath = await acquirer.AcquireAsync(feed, PackageId, Version, CancellationToken.None);
        }

        await Assert.ThrowsAsync<FeedCredentialUnavailableException>(
            () => acquirer.AcquireAsync(feed, PackageId, Version, CancellationToken.None));

        Assert.True(Directory.Exists(installPath));
        Assert.Equal(1, server.PackageDownloads);
    }

    [Fact]
    public async Task EnumerateVersionsAsync_WithAResolvableReference_AuthenticatesAndListsVersions()
    {
        await using var server = StartServer(new(FeedCredential.TokenUserName, Sentinel));
        using var variable = new EnvironmentVariableScope(_variableName, Sentinel);

        var versions = await CreateEnumerator().EnumerateVersionsAsync(Feed(server), PackageId, CancellationToken.None);

        Assert.Equal([Version], versions.Versions);
        Assert.True(server.AuthorizedRequests > 0, "the NuGet client never authenticated");
    }

    [Fact]
    public async Task EnumerateVersionsAsync_WithAnUnresolvableReference_RefusesTheFeedByNameWithoutContactingIt()
    {
        await using var server = StartServer(new(FeedCredential.TokenUserName, Sentinel));

        var exception = await Assert.ThrowsAsync<FeedCredentialUnavailableException>(
            () => CreateEnumerator().EnumerateVersionsAsync(Feed(server), PackageId, CancellationToken.None));

        Assert.Equal(FeedName, exception.FeedName);
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task FeedAccess_WhateverTheOutcome_NeverNamesTheSecret()
    {
        await using var server = StartServer(new(FeedCredential.TokenUserName, Sentinel));
        var feed = Feed(server);
        var acquirer = CreateAcquirer();
        var messages = new List<string>();

        using (var variable = new EnvironmentVariableScope(_variableName, Sentinel))
        {
            var installPath = await acquirer.AcquireAsync(feed, PackageId, Version, CancellationToken.None);
            var versions = await CreateEnumerator().EnumerateVersionsAsync(feed, PackageId, CancellationToken.None);
            messages.Add(installPath);
            messages.Add(string.Join(",", versions.Versions));
            messages.Add(feed.ToString());
        }

        messages.Add(
            (await Assert.ThrowsAsync<FeedCredentialUnavailableException>(
                () => acquirer.AcquireAsync(feed, PackageId, Version, CancellationToken.None))).ToString());
        messages.Add(
            (await Assert.ThrowsAsync<FeedCredentialUnavailableException>(
                () => CreateEnumerator().EnumerateVersionsAsync(feed, PackageId, CancellationToken.None))).ToString());

        Assert.DoesNotContain(Sentinel, string.Join(Environment.NewLine, messages), StringComparison.Ordinal);
    }

    private TestNuGetFeedServer StartServer(TestFeedBasicAuth requiredAuth) =>
        new(PackageId, Version, _packageBytes, requiredAuth: requiredAuth);

    private FeedDefinition Feed(TestNuGetFeedServer server) =>
        new(FeedName, server.ServiceIndexUri, $"secrets://env/{_variableName}");

    private NuGetRemotePackageAcquirer CreateAcquirer() => new(Options(), Resolver());

    private NuGetFeedVersionEnumerator CreateEnumerator() => new(Options(), Resolver());

    private IOptions<FeedResolutionOptions> Options() =>
        new OptionsWrapper<FeedResolutionOptions>(new() { PackageInstallRoot = Path.Combine(_temp.Path, "installed") });

    private static ISecretReferenceResolver Resolver() => new SecretReferenceResolver([new EnvironmentSecretReferenceProvider()]);
}
