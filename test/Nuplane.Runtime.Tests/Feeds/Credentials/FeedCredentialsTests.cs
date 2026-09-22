using Nuplane.Abstractions;
using Nuplane.Feeds.Credentials;
using Nuplane.Runtime.Tests.TestSupport;

namespace Nuplane.Runtime.Tests.Feeds.Credentials;

/// <summary>
/// The shape rules for a resolved secret, and the lifetime of the per-cycle cache that decides how
/// often one is read.
/// </summary>
public sealed class FeedCredentialsTests
{
    private const string Reference = "secrets://store/feed-token";

    private readonly FeedDefinition _feed = new("private-feed", new("https://packages.example.com/v3/index.json"), Reference);

    [Fact]
    public async Task ResolveAsync_WithAUserPasswordSecret_SplitsAtTheFirstColon()
    {
        var credential = await ResolveAsync("deploy:p4ss:word");

        Assert.Equal("deploy", credential!.UserName);
        Assert.Equal("p4ss:word", credential.Password.Reveal());
    }

    [Fact]
    public async Task ResolveAsync_WithABareToken_SendsItUnderThePlaceholderUserName()
    {
        var credential = await ResolveAsync("a-bare-token");

        Assert.Equal(FeedCredential.TokenUserName, credential!.UserName);
        Assert.Equal("a-bare-token", credential.Password.Reveal());
    }

    [Theory]
    [InlineData(":password-only")]
    [InlineData("user-only:")]
    public async Task ResolveAsync_WithAHalfEmptyUserPasswordSecret_RefusesInsteadOfSendingIt(string secret)
    {
        var lookup = await LookupAsync(secret);

        Assert.True(lookup.IsRefused);
    }

    [Fact]
    public async Task ResolveAsync_WhenNoProviderClaimsTheReference_Refuses()
    {
        var resolver = new SecretReferenceResolver([StubSecretReferenceProvider.Holding("elsewhere", "feed-token", "value")]);

        var lookup = await FeedCredentials.ResolveAsync(resolver, _feed, CancellationToken.None);

        Assert.True(lookup.IsRefused);
        Assert.True(lookup.DeclaresCredentials);
    }

    [Fact]
    public async Task ResolveAsync_WithNoResolverAtAll_Refuses()
    {
        var lookup = await FeedCredentials.ResolveAsync(null, _feed, CancellationToken.None);

        Assert.True(lookup.IsRefused);
    }

    [Fact]
    public async Task ResolveAsync_ForAFeedThatDeclaresNoCredentials_ReportsNothingToResolve()
    {
        var anonymous = new FeedDefinition("public-feed", new("https://packages.example.com/v3/index.json"));

        var lookup = await FeedCredentials.ResolveAsync(null, anonymous, CancellationToken.None);

        Assert.False(lookup.DeclaresCredentials);
        Assert.False(lookup.IsRefused);
        Assert.Null(lookup.Credential);
    }

    [Fact]
    public async Task ResolveAsync_InsideOneCycle_ReadsTheSecretOnce()
    {
        var provider = StubSecretReferenceProvider.Holding("store", "feed-token", "a-bare-token");
        var resolver = new SecretReferenceResolver([provider]);

        using (FeedCredentials.BeginCycle())
        {
            for (var i = 0; i < 5; i++)
            {
                Assert.False((await FeedCredentials.ResolveAsync(resolver, _feed, CancellationToken.None)).IsRefused);
            }
        }

        Assert.Equal(1, provider.Lookups);
    }

    [Fact]
    public async Task ResolveAsync_InALaterCycle_ReadsTheSecretAgain()
    {
        // The cache must not outlive the cycle that filled it: a secret rotated between two cycles
        // has to be picked up by the second, and no resolved secret may linger in memory afterwards.
        var provider = StubSecretReferenceProvider.Holding("store", "feed-token", "a-bare-token");
        var resolver = new SecretReferenceResolver([provider]);

        using (FeedCredentials.BeginCycle())
        {
            await FeedCredentials.ResolveAsync(resolver, _feed, CancellationToken.None);
        }

        using (FeedCredentials.BeginCycle())
        {
            await FeedCredentials.ResolveAsync(resolver, _feed, CancellationToken.None);
        }

        Assert.Equal(2, provider.Lookups);
    }

    [Fact]
    public async Task ResolveAsync_OutsideACycle_ReadsTheSecretEveryTime()
    {
        var provider = StubSecretReferenceProvider.Holding("store", "feed-token", "a-bare-token");
        var resolver = new SecretReferenceResolver([provider]);

        await FeedCredentials.ResolveAsync(resolver, _feed, CancellationToken.None);
        await FeedCredentials.ResolveAsync(resolver, _feed, CancellationToken.None);

        Assert.Equal(2, provider.Lookups);
    }

    [Fact]
    public async Task ResolveAsync_InsideOneCycle_DoesNotServeOneFeedsSecretToAnother()
    {
        var provider = new StubSecretReferenceProvider(
            "store",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["feed-token"] = "first-token",
                ["other-token"] = "second-token"
            });
        var resolver = new SecretReferenceResolver([provider]);
        var other = new FeedDefinition("other-feed", new("https://other.example.com/v3/index.json"), "secrets://store/other-token");

        using (FeedCredentials.BeginCycle())
        {
            var first = await FeedCredentials.ResolveAsync(resolver, _feed, CancellationToken.None);
            var second = await FeedCredentials.ResolveAsync(resolver, other, CancellationToken.None);

            Assert.Equal("first-token", first.Credential!.Password.Reveal());
            Assert.Equal("second-token", second.Credential!.Password.Reveal());
        }

        Assert.Equal(2, provider.Lookups);
    }

    [Fact]
    public async Task ToString_OfAResolvedCredential_RedactsBothHalves()
    {
        var lookup = await LookupAsync("deploy:sentinel-secret-cred-9f3a");

        Assert.DoesNotContain("sentinel-secret-cred-9f3a", $"{lookup} {lookup.Credential}", StringComparison.Ordinal);
        Assert.DoesNotContain("deploy", $"{lookup} {lookup.Credential}", StringComparison.Ordinal);
    }

    private Task<FeedCredentialLookup> LookupAsync(string secret)
    {
        var resolver = new SecretReferenceResolver([StubSecretReferenceProvider.Holding("store", "feed-token", secret)]);
        return FeedCredentials.ResolveAsync(resolver, _feed, CancellationToken.None).AsTask();
    }

    private async Task<FeedCredential?> ResolveAsync(string secret) => (await LookupAsync(secret)).Credential;
}
