using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Nuplane.Feeds.Credentials;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;
using Nuplane.Runtime.Tests.TestSupport;

namespace Nuplane.Runtime.Tests.Feeds.Credentials;

/// <summary>
/// The whole path, end to end, against a feed that serves nothing without basic authentication:
/// a composed host reconciling, and the host-free <see cref="NuplaneRestore"/> entry point, with the
/// secret behind <c>secrets://env/…</c> present and absent.
/// </summary>
public sealed class CredentialedFeedRestoreTests : IDisposable
{
    private const string PackageId = "MyPlugin";
    private const string Version = "1.0.0";
    private const string FeedName = "private-feed";
    private const string Sentinel = "sentinel-restore-token-2e7d40";

    private readonly TempDirectory _temp = new();
    private readonly byte[] _packageBytes = NupkgTestBuilder.Create(PackageId, Version).Build();
    private readonly string _variableName = "NUPLANE_TEST_FEED_TOKEN_" + Guid.NewGuid().ToString("N");
    private readonly CapturingLoggerProvider _logs = new();
    private readonly string _installRoot;
    private readonly string _stateFilePath;

    public CredentialedFeedRestoreTests()
    {
        _installRoot = Path.Combine(_temp.Path, "packages");
        _stateFilePath = Path.Combine(_temp.Path, "state", "store-state.json");
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task RestoreAsync_WithTheReferencedVariableSet_RestoresFromTheAuthenticatedFeed()
    {
        await using var server = StartServer();
        using var variable = new EnvironmentVariableScope(_variableName, Sentinel);

        var result = await NuplaneRestore.RestoreAsync(Configuration(server), RestoreOptions());

        Assert.Empty(result.CredentialRefusedFeeds);
        Assert.Empty(result.FailedPackages);
        Assert.False(result.IsDegraded);
        Assert.Equal(PackageId, Assert.Single(result.ActivePackages).PackageId);
        Assert.Equal(1, server.PackageDownloads);
        Assert.True(server.AuthorizedRequests > 0, "the feed was never contacted with credentials");
    }

    [Fact]
    public async Task RestoreAsync_WithTheReferencedVariableUnset_NamesTheFeedAndContactsNothing()
    {
        await using var server = StartServer();

        var result = await NuplaneRestore.RestoreAsync(Configuration(server), RestoreOptions());

        Assert.Equal(FeedName, Assert.Single(result.CredentialRefusedFeeds));
        // The desired set still asks for the package: the feed is gone from resolution, not from the
        // desired state, so the refusal is what stops it rather than an absence of anything to do.
        Assert.Equal(PackageId, Assert.Single(result.FailedPackages));
        Assert.Empty(result.ActivePackages);
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task DescribeDesiredAsync_WithTheReferencedVariableSet_DoesNotRefuseTheFeed()
    {
        await using var server = StartServer();
        using var variable = new EnvironmentVariableScope(_variableName, Sentinel);

        var description = await NuplaneRestore.DescribeDesiredAsync(Configuration(server), RestoreOptions());

        Assert.Empty(description.CredentialRefusedFeeds);
        Assert.Equal(PackageId, Assert.Single(description.Requests).PackageId);
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task RestoreAsync_WithAProviderRegisteredThroughConfigureBuilder_RestoresFromTheAuthenticatedFeed()
    {
        // The host-free path's registration hook: a caller whose secrets live somewhere other than
        // the environment adds its own provider here, and the reference names that provider.
        await using var server = StartServer();
        var options = RestoreOptions();
        options.ConfigureBuilder += (builder, _) => builder.Services.AddSingleton<ISecretReferenceProvider>(
            StubSecretReferenceProvider.Holding("test-store", "feed-token", Sentinel));

        var result = await NuplaneRestore.RestoreAsync(
            Configuration(server, reference: "secrets://test-store/feed-token"),
            options);

        Assert.Empty(result.CredentialRefusedFeeds);
        Assert.Equal(PackageId, Assert.Single(result.ActivePackages).PackageId);
        Assert.True(server.AuthorizedRequests > 0, "the feed was never contacted with credentials");
    }

    [Fact]
    public async Task RestoreAsync_WhetherOrNotTheSecretResolves_NeverWritesItToLogsOrResults()
    {
        await using var server = StartServer();
        var reported = new List<string>();

        using (var variable = new EnvironmentVariableScope(_variableName, Sentinel))
        {
            reported.Add(Describe(await NuplaneRestore.RestoreAsync(Configuration(server), RestoreOptions())));
        }

        reported.Add(Describe(await NuplaneRestore.RestoreAsync(Configuration(server), RestoreOptions())));

        Assert.DoesNotContain(Sentinel, _logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, string.Join(Environment.NewLine, reported), StringComparison.Ordinal);
        Assert.NotEmpty(_logs.Entries);
    }

    [Fact]
    public async Task AddNuplane_InAComposedHost_ReconcilesFromTheAuthenticatedFeed()
    {
        await using var server = StartServer();
        using var variable = new EnvironmentVariableScope(_variableName, Sentinel);
        await using var provider = ComposeHost(server);

        var run = await provider.GetRequiredService<IReconciliationService>()
            .TriggerAsync(ReconciliationTrigger.Manual(), CancellationToken.None);

        Assert.False(run.IsDegraded);
        Assert.Empty(run.FailedPackages);
        Assert.Equal(PackageId, Assert.Single(await NuplaneStore.ReadActivePackagesAsync(_stateFilePath)).PackageId);
        Assert.True(server.AuthorizedRequests > 0, "the feed was never contacted with credentials");
        Assert.DoesNotContain(Sentinel, _logs.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddNuplane_InAComposedHost_WithTheReferencedVariableUnset_FailsThePackageWithoutContactingTheFeed()
    {
        await using var server = StartServer();
        await using var provider = ComposeHost(server);

        var run = await provider.GetRequiredService<IReconciliationService>()
            .TriggerAsync(ReconciliationTrigger.Manual(), CancellationToken.None);

        Assert.True(run.IsDegraded);
        Assert.Equal(PackageId, Assert.Single(run.FailedPackages));
        Assert.Equal(0, server.Requests);
        Assert.Contains(_logs.Entries, entry => entry.Contains(FeedName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddNuplane_InAComposedHost_ResolvesOneFeedsSecretOnceForTheWholeCycle()
    {
        await using var server = StartServer();
        using var variable = new EnvironmentVariableScope(_variableName, Sentinel);
        var counting = StubSecretReferenceProvider.Holding("env", _variableName, Sentinel);
        await using var provider = ComposeHost(server, services =>
        {
            services.RemoveAll<ISecretReferenceProvider>();
            services.AddSingleton<ISecretReferenceProvider>(counting);
        });

        await provider.GetRequiredService<IReconciliationService>()
            .TriggerAsync(ReconciliationTrigger.Manual(), CancellationToken.None);

        // Version enumeration and acquisition both need the credential, and both ran: one lookup
        // between them is the per-cycle cache doing its job.
        Assert.Equal(1, counting.Lookups);
        Assert.Equal(1, server.PackageDownloads);
    }

    private static string Describe(NuplaneRestoreResult result) =>
        $"{string.Join(",", result.CredentialRefusedFeeds)} {string.Join(",", result.FailedPackages)} "
        + $"{string.Join(",", result.ActivePackages.Select(package => $"{package.PackageId}:{package.Version}:{package.InstallPath}"))} "
        + $"{result.InstallRoot} {result.StateFilePath}";

    private TestNuGetFeedServer StartServer() =>
        new(PackageId, Version, _packageBytes, requiredAuth: new(FeedCredential.TokenUserName, Sentinel));

    private ServiceProvider ComposeHost(TestNuGetFeedServer server, Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(_logs);
        });
        services.AddNuplane(Configuration(server), builder =>
        {
            CredentialedFeedTestComposition.AllowHttpLoopbackFeeds(builder.Services);
            configureServices?.Invoke(builder.Services);
        });

        return services.BuildServiceProvider();
    }

    private NuplaneRestoreOptions RestoreOptions() =>
        new()
        {
            InstallRoot = _installRoot,
            StateFilePath = _stateFilePath,
            LoggerFactory = _logs.CreateFactory(),
            ConfigureBuilder = (builder, _) => CredentialedFeedTestComposition.AllowHttpLoopbackFeeds(builder.Services)
        };

    /// <summary>
    /// Nuplane's own configuration section, as a host would write it: the feed names where its
    /// secret lives, never the secret. The version range is deliberately not a single pin, so the
    /// cycle goes through version enumeration — its own authenticated call — before acquiring.
    /// </summary>
    private IConfiguration Configuration(TestNuGetFeedServer server, string? reference = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Setup:Feeds:{FeedName}:ServiceIndex"] = server.ServiceIndexUri.AbsoluteUri,
                [$"Setup:Feeds:{FeedName}:Credentials"] = reference ?? $"secrets://env/{_variableName}",
                [$"Setup:Feeds:{FeedName}:IncludePatterns:0"] = $"{PackageId} [1.0.0,2.0.0)",
                ["FeedResolution:PackageInstallRoot"] = _installRoot,
                ["StoreRegistry:StateFilePath"] = _stateFilePath
            })
            .Build();
}
