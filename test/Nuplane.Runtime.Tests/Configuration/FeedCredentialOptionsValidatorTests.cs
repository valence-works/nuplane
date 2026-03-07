using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Feeds.Configuration;

namespace Nuplane.Runtime.Tests.Configuration;

public sealed class FeedCredentialOptionsValidatorTests
{
    private readonly FeedCredentialOptionsValidator _validator = new();

    private static FeedResolutionOptions FeedOptions(params FeedDefinition[] feeds)
    {
        var opts = new FeedResolutionOptions();
        foreach (var f in feeds) opts.Feeds.Add(f);
        return opts;
    }

    private static FeedTrustPolicyOptions DefaultTrustPolicy() => new();
    private static SourceTrustOptions DefaultSourceTrust() => new();

    [Fact]
    public void FileUri_WithoutCredentials_IsValid()
    {
        var feed = new FeedDefinition("local-drop", new("file:///var/nuplane/drop/"), FeedTrustLevel.Trusted);
        var errors = _validator.Validate(FeedOptions(feed), DefaultTrustPolicy(), DefaultSourceTrust());
        Assert.Empty(errors);
    }

    [Fact]
    public void FileUri_WithCredentials_IsRejected()
    {
        var feed = new FeedDefinition("local-drop", new("file:///var/nuplane/drop/"), FeedTrustLevel.Trusted, "secrets://some-secret");
        var errors = _validator.Validate(FeedOptions(feed), DefaultTrustPolicy(), DefaultSourceTrust());
        Assert.Single(errors);
        Assert.Contains("file://", errors[0]);
        Assert.Contains("must not configure credentials", errors[0]);
    }

    [Fact]
    public void HttpsFeed_IsStillValid()
    {
        var feed = new FeedDefinition("nuget-org", new("https://api.nuget.org/v3/index.json"), FeedTrustLevel.Trusted);
        var errors = _validator.Validate(FeedOptions(feed), DefaultTrustPolicy(), DefaultSourceTrust());
        Assert.Empty(errors);
    }

    [Fact]
    public void HttpFeed_IsRejected()
    {
        var feed = new FeedDefinition("insecure", new("http://example.com/v3/index.json"), FeedTrustLevel.Trusted);
        var errors = _validator.Validate(FeedOptions(feed), DefaultTrustPolicy(), DefaultSourceTrust());
        Assert.Single(errors);
        Assert.Contains("HTTPS", errors[0]);
    }

    [Fact]
    public void FileUri_DoesNotRequireHttps()
    {
        var feed = new FeedDefinition("local", new("file:///tmp/packages/"), FeedTrustLevel.Trusted);
        var errors = _validator.Validate(FeedOptions(feed), DefaultTrustPolicy(), DefaultSourceTrust());
        Assert.DoesNotContain(errors, e => e.Contains("HTTPS"));
    }

    [Fact]
    public void MixedFeeds_ValidatesEachCorrectly()
    {
        var localFeed = new FeedDefinition("local", new("file:///tmp/packages/"), FeedTrustLevel.Trusted);
        var remoteFeed = new FeedDefinition("remote", new("https://api.nuget.org/v3/index.json"), FeedTrustLevel.Trusted);
        var errors = _validator.Validate(FeedOptions(localFeed, remoteFeed), DefaultTrustPolicy(), DefaultSourceTrust());
        Assert.Empty(errors);
    }

    [Fact]
    public void DuplicateFeedNames_AreRejected()
    {
        var f1 = new FeedDefinition("dupe", new("file:///a/"), FeedTrustLevel.Trusted);
        var f2 = new FeedDefinition("dupe", new("file:///b/"), FeedTrustLevel.Trusted);
        var errors = _validator.Validate(FeedOptions(f1, f2), DefaultTrustPolicy(), DefaultSourceTrust());
        Assert.Contains(errors, e => e.Contains("Duplicate"));
    }

    [Fact]
    public void EmptyFeedName_IsRejected()
    {
        var feed = new FeedDefinition("", new("file:///a/"), FeedTrustLevel.Trusted);
        var errors = _validator.Validate(FeedOptions(feed), DefaultTrustPolicy(), DefaultSourceTrust());
        Assert.Contains(errors, e => e.Contains("required"));
    }
}
