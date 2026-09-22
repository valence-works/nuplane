using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nuplane.Feeds.Credentials;
using Nuplane.Hosting;
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

    [Fact]
    public async Task StartAsync_WithASecondProviderClaimingEnv_FailsWhileTheHostIsStarting()
    {
        // The ambiguity must not wait for the first cycle that happens to touch a credentialed feed:
        // no feed here references a secret at all, and the host still refuses to start. The startup
        // hosted service takes the resolver as an explicit dependency so that this stays true
        // whichever other dependency edge — today the package acquirer's — would have constructed it
        // first.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(builder =>
            builder.Services.AddSingleton<ISecretReferenceProvider>(
                StubSecretReferenceProvider.Holding("env", _variableName, Sentinel)));
        await using var provider = services.BuildServiceProvider();
        // Bounded, so that a startup service which failed to refuse fails this test quickly instead
        // of waiting forever on a trigger dispatcher the test deliberately never starts.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetServices<IHostedService>()
                .OfType<NuplaneStartupHostedService>()
                .Single()
                .StartAsync(timeout.Token));

        // The distinctive wording, so this can only be the resolver refusing the ambiguity rather
        // than some other startup failure that happens to mention the feed's provider.
        Assert.Contains("both claim the provider name 'env'", exception.Message, StringComparison.Ordinal);
    }
}
