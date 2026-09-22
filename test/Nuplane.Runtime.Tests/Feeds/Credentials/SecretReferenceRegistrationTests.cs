using Microsoft.Extensions.DependencyInjection;
using Nuplane.Feeds.Credentials;
using Nuplane.Runtime.Tests.TestSupport;

namespace Nuplane.Runtime.Tests.Feeds.Credentials;

/// <summary>
/// What <c>AddNuplane</c> puts in the container for secret resolution, and how a host replaces it.
/// </summary>
public sealed class SecretReferenceRegistrationTests
{
    private const string Sentinel = "sentinel-registration-token-7c5e01";

    private readonly string _variableName = "NUPLANE_TEST_FEED_TOKEN_" + Guid.NewGuid().ToString("N");

    [Fact]
    public void AddNuplane_RegistersTheResolverAndTheEnvProvider()
    {
        var services = new ServiceCollection();

        services.AddNuplane(_ => { });

        using var provider = services.BuildServiceProvider();
        Assert.IsType<SecretReferenceResolver>(provider.GetRequiredService<ISecretReferenceResolver>());
        Assert.IsType<EnvironmentSecretReferenceProvider>(Assert.Single(provider.GetServices<ISecretReferenceProvider>()));
    }

    [Fact]
    public void AddNuplane_CalledTwice_RegistersOneEnvProvider()
    {
        var services = new ServiceCollection();

        services.AddNuplane(_ => { });
        services.AddNuplane(_ => { });

        using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<ISecretReferenceProvider>());
    }

    [Fact]
    public async Task AddNuplane_WithAHostProviderAdded_DispatchesToBoth()
    {
        var services = new ServiceCollection();
        services.AddNuplane(builder =>
            builder.Services.AddSingleton<ISecretReferenceProvider>(
                StubSecretReferenceProvider.Holding("vault", "feed-token", Sentinel)));
        using var variable = new EnvironmentVariableScope(_variableName, "from-the-environment");

        using var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<ISecretReferenceResolver>();

        Assert.Equal(
            "from-the-environment",
            (await resolver.ResolveAsync($"secrets://env/{_variableName}", CancellationToken.None)).Value!.Reveal());
        Assert.Equal(
            Sentinel,
            (await resolver.ResolveAsync("secrets://vault/feed-token", CancellationToken.None)).Value!.Reveal());
    }

    [Fact]
    public async Task AddNuplane_AfterRemovingTheEnvDescriptor_UsesTheReplacementProvider()
    {
        // The documented replacement path: remove the built-in descriptor, then register your own
        // provider for the same name. Adding a second 'env' provider without removing this one is
        // refused when the resolver is built, so a replacement that did not take cannot pass unseen.
        var services = new ServiceCollection();
        services.AddNuplane(builder =>
        {
            builder.Services.Remove(builder.Services.Single(descriptor =>
                descriptor.ServiceType == typeof(ISecretReferenceProvider)
                && descriptor.ImplementationType == typeof(EnvironmentSecretReferenceProvider)));
            builder.Services.AddSingleton<ISecretReferenceProvider>(
                StubSecretReferenceProvider.Holding("env", _variableName, Sentinel));
        });
        using var variable = new EnvironmentVariableScope(_variableName, "from-the-environment");

        using var provider = services.BuildServiceProvider();
        var resolution = await provider.GetRequiredService<ISecretReferenceResolver>()
            .ResolveAsync($"secrets://env/{_variableName}", CancellationToken.None);

        Assert.Equal(Sentinel, resolution.Value!.Reveal());
        Assert.IsType<StubSecretReferenceProvider>(Assert.Single(provider.GetServices<ISecretReferenceProvider>()));
    }

    [Fact]
    public void AddNuplane_WithASecondProviderClaimingEnv_RefusesToBuildTheResolver()
    {
        var services = new ServiceCollection();
        services.AddNuplane(builder =>
            builder.Services.AddSingleton<ISecretReferenceProvider>(
                StubSecretReferenceProvider.Holding("env", _variableName, Sentinel)));

        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ISecretReferenceResolver>());
        Assert.Contains("env", exception.Message, StringComparison.Ordinal);
    }
}
