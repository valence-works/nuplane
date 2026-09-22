using Nuplane.Feeds.Credentials;
using Nuplane.Runtime.Tests.TestSupport;

namespace Nuplane.Runtime.Tests.Feeds.Credentials;

public sealed class EnvironmentSecretReferenceProviderTests
{
    private const string Sentinel = "sentinel-secret-env-4d2b7e";

    private readonly string _variableName = "NUPLANE_TEST_FEED_TOKEN_" + Guid.NewGuid().ToString("N");
    private readonly EnvironmentSecretReferenceProvider _provider = new();

    [Fact]
    public void Scheme_IsEnv() => Assert.Equal("env", _provider.Scheme);

    [Fact]
    public async Task ResolveAsync_WithTheVariableSet_ReturnsItsValue()
    {
        using var variable = new EnvironmentVariableScope(_variableName, Sentinel);

        Assert.Equal(Sentinel, await _provider.ResolveAsync(_variableName, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_WithTheVariableUnset_ReturnsNull() =>
        Assert.Null(await _provider.ResolveAsync(_variableName, CancellationToken.None));

    [Fact]
    public async Task ResolveAsync_ThroughTheResolver_ResolvesAnEnvReference()
    {
        using var variable = new EnvironmentVariableScope(_variableName, Sentinel);
        var resolver = new SecretReferenceResolver([_provider]);

        var resolution = await resolver.ResolveAsync($"secrets://env/{_variableName}", CancellationToken.None);

        Assert.True(resolution.IsResolved);
        Assert.Equal(Sentinel, resolution.Value!.Reveal());
    }

    [Fact]
    public async Task ResolveAsync_ThroughTheResolver_WithTheVariableUnset_IsUnresolvedRatherThanAFailure()
    {
        var resolver = new SecretReferenceResolver([_provider]);

        var resolution = await resolver.ResolveAsync($"secrets://env/{_variableName}", CancellationToken.None);

        Assert.Equal(SecretReferenceResolutionStatus.Empty, resolution.Status);
        Assert.Null(resolution.Value);
    }

    [Fact]
    public async Task ResolveAsync_WithABlankName_Throws() =>
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await _provider.ResolveAsync(" ", CancellationToken.None));
}
