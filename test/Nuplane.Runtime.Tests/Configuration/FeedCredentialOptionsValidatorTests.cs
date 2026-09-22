using Nuplane.Abstractions;
using Nuplane.Feeds.Configuration;

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

    [Fact]
    public void FileUri_WithoutCredentials_IsValid()
    {
        var feed = new FeedDefinition("local-drop", new("file:///var/nuplane/drop/"));
        var errors = _validator.Validate(FeedOptions(feed));
        Assert.Empty(errors);
    }

    [Fact]
    public void FileUri_WithCredentials_IsRejected()
    {
        var feed = new FeedDefinition("local-drop", new("file:///var/nuplane/drop/"), "secrets://some-secret");
        var errors = _validator.Validate(FeedOptions(feed));
        Assert.Single(errors);
        Assert.Contains("file://", errors[0]);
        Assert.Contains("must not configure credentials", errors[0]);
    }

    [Fact]
    public void HttpsFeed_IsStillValid()
    {
        var feed = new FeedDefinition("nuget-org", new("https://api.nuget.org/v3/index.json"));
        var errors = _validator.Validate(FeedOptions(feed));
        Assert.Empty(errors);
    }

    [Fact]
    public void HttpFeed_IsRejected()
    {
        var feed = new FeedDefinition("insecure", new("http://example.com/v3/index.json"));
        var errors = _validator.Validate(FeedOptions(feed));
        Assert.Single(errors);
        Assert.Contains("HTTPS", errors[0]);
    }

    [Fact]
    public void FileUri_DoesNotRequireHttps()
    {
        var feed = new FeedDefinition("local", new("file:///tmp/packages/"));
        var errors = _validator.Validate(FeedOptions(feed));
        Assert.DoesNotContain(errors, e => e.Contains("HTTPS"));
    }

    [Fact]
    public void HttpsFeed_WithAWellFormedSecretReference_IsValid()
    {
        var feed = new FeedDefinition("private", new("https://packages.example.com/v3/index.json"), "secrets://env/MY_FEED_TOKEN");
        var errors = _validator.Validate(FeedOptions(feed));
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("a-raw-token")]
    [InlineData("secrets://env")]
    [InlineData("secrets://env/")]
    [InlineData("secrets:///MY_FEED_TOKEN")]
    public void HttpsFeed_WithAMalformedSecretReference_IsRejectedWithoutEchoingTheValue(string credentials)
    {
        // A host that pasted the token itself into Credentials lands here, so the rejected value
        // must not appear in the error that is logged and surfaced.
        var feed = new FeedDefinition("private", new("https://packages.example.com/v3/index.json"), credentials);

        var error = Assert.Single(_validator.Validate(FeedOptions(feed)));

        Assert.Contains("secrets://<provider>/<name>", error, StringComparison.Ordinal);
        Assert.DoesNotContain(credentials, error, StringComparison.Ordinal);
    }

    [Fact]
    public void MixedFeeds_ValidatesEachCorrectly()
    {
        var localFeed = new FeedDefinition("local", new("file:///tmp/packages/"));
        var remoteFeed = new FeedDefinition("remote", new("https://api.nuget.org/v3/index.json"));
        var errors = _validator.Validate(FeedOptions(localFeed, remoteFeed));
        Assert.Empty(errors);
    }

    [Fact]
    public void DuplicateFeedNames_AreRejected()
    {
        var f1 = new FeedDefinition("dupe", new("file:///a/"));
        var f2 = new FeedDefinition("dupe", new("file:///b/"));
        var errors = _validator.Validate(FeedOptions(f1, f2));
        Assert.Contains(errors, e => e.Contains("Duplicate"));
    }

    [Fact]
    public void EmptyFeedName_IsRejected()
    {
        var feed = new FeedDefinition("", new("file:///a/"));
        var errors = _validator.Validate(FeedOptions(feed));
        Assert.Contains(errors, e => e.Contains("required"));
    }
}
