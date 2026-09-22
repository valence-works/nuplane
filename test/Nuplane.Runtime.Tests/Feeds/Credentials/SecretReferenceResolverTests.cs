using Nuplane.Feeds.Credentials;
using Nuplane.Runtime.Tests.TestSupport;

namespace Nuplane.Runtime.Tests.Feeds.Credentials;

public sealed class SecretReferenceResolverTests
{
    private const string Sentinel = "sentinel-secret-6a1f9c";

    private readonly StubSecretReferenceProvider _environment = StubSecretReferenceProvider.Holding("env", "MY_FEED_TOKEN", Sentinel);
    private readonly StubSecretReferenceProvider _vault = StubSecretReferenceProvider.Holding("vault", "apps/nuplane/token", "vault-value");
    private readonly SecretReferenceResolver _resolver;

    public SecretReferenceResolverTests() => _resolver = new([_environment, _vault]);

    [Fact]
    public async Task ResolveAsync_WithTwoProviders_DispatchesByProviderName()
    {
        var resolution = await _resolver.ResolveAsync("secrets://env/MY_FEED_TOKEN", CancellationToken.None);

        Assert.Equal(SecretReferenceResolutionStatus.Resolved, resolution.Status);
        Assert.Equal("env", resolution.ProviderName);
        Assert.Equal(Sentinel, resolution.Value!.Reveal());
        Assert.Equal(1, _environment.Lookups);
        Assert.Equal(0, _vault.Lookups);
    }

    [Fact]
    public async Task ResolveAsync_WithTheProviderNameInAnotherCase_StillDispatches()
    {
        var resolution = await _resolver.ResolveAsync("SECRETS://ENV/MY_FEED_TOKEN", CancellationToken.None);

        Assert.True(resolution.IsResolved);
        Assert.Equal(Sentinel, resolution.Value!.Reveal());
    }

    [Fact]
    public async Task ResolveAsync_WithANameContainingSlashes_PassesTheWholeNameToTheProvider()
    {
        var resolution = await _resolver.ResolveAsync("secrets://vault/apps/nuplane/token", CancellationToken.None);

        Assert.True(resolution.IsResolved);
        Assert.Equal("apps/nuplane/token", Assert.Single(_vault.RequestedNames));
    }

    [Fact]
    public async Task ResolveAsync_WithTheSecretNameInAnotherCase_DoesNotRewriteIt()
    {
        // Environment variable names are case-sensitive on Unix, so the name half must reach the
        // provider exactly as configured — a URI-style parse would have lower-cased the host segment
        // only, and this proves the name segment survives untouched.
        await _resolver.ResolveAsync("secrets://env/my_feed_token", CancellationToken.None);

        Assert.Equal("my_feed_token", Assert.Single(_environment.RequestedNames));
    }

    [Fact]
    public async Task ResolveAsync_WhenNoProviderClaimsTheProviderName_ReportsNoProviderWithoutThrowing()
    {
        var resolution = await _resolver.ResolveAsync("secrets://nowhere/MY_FEED_TOKEN", CancellationToken.None);

        Assert.Equal(SecretReferenceResolutionStatus.NoProvider, resolution.Status);
        Assert.Equal("nowhere", resolution.ProviderName);
        Assert.Null(resolution.Value);
    }

    [Theory]
    [InlineData("MISSING")]
    [InlineData("EMPTY")]
    public async Task ResolveAsync_WhenTheProviderHoldsNoValue_ReportsEmptyWithoutThrowing(string name)
    {
        var provider = new StubSecretReferenceProvider(
            "store",
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["EMPTY"] = string.Empty });
        var resolver = new SecretReferenceResolver([provider]);

        var resolution = await resolver.ResolveAsync($"secrets://store/{name}", CancellationToken.None);

        Assert.Equal(SecretReferenceResolutionStatus.Empty, resolution.Status);
        Assert.Null(resolution.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("MY_FEED_TOKEN")]
    [InlineData("secrets:/env/MY_FEED_TOKEN")]
    [InlineData("secrets://")]
    [InlineData("secrets://env")]
    [InlineData("secrets://env/")]
    [InlineData("secrets:///MY_FEED_TOKEN")]
    public async Task ResolveAsync_WithAMalformedReference_Throws(string reference)
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            async () => await _resolver.ResolveAsync(reference, CancellationToken.None));

        Assert.Contains(SecretReference.ExpectedShape, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_WithAMalformedReference_DoesNotEchoIt()
    {
        // A host that pastes the token itself into Credentials lands exactly here, so echoing the
        // rejected value would print the secret the reference syntax exists to keep out of config.
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            async () => await _resolver.ResolveAsync(Sentinel, CancellationToken.None));

        Assert.DoesNotContain(Sentinel, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_WithTwoProvidersClaimingOneName_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new SecretReferenceResolver([_environment, StubSecretReferenceProvider.Holding("ENV", "OTHER", "value")]));

        Assert.Contains("ENV", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_WithAProviderDeclaringNoName_Throws() =>
        Assert.Throws<InvalidOperationException>(
            () => new SecretReferenceResolver([StubSecretReferenceProvider.Holding(" ", "NAME", "value")]));

    [Fact]
    public async Task ToString_OfAResolvedOutcome_RedactsTheSecret()
    {
        var resolution = await _resolver.ResolveAsync("secrets://env/MY_FEED_TOKEN", CancellationToken.None);

        Assert.DoesNotContain(Sentinel, resolution.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, resolution.Value!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, $"{resolution} {resolution.Value}", StringComparison.Ordinal);
    }

    [Fact]
    public void SecretValue_WithAnEmptyValue_Throws() => Assert.Throws<ArgumentException>(() => new SecretValue(string.Empty));
}
