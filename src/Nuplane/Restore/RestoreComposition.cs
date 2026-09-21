using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Feeds;
using Nuplane.Feeds.Configuration;
using Nuplane.Reconciliation.LockFile;
using Nuplane.Sources;
using Nuplane.Store.State;
using Nuplane.Versioning;

namespace Nuplane.Restore;

/// <summary>
/// The throwaway Nuplane composition a host-free restore runs inside: the same services
/// <c>AddNuplane</c> gives a host, with every store path pinned to an absolute location, no loading
/// registered, and no host started.
/// </summary>
internal sealed class RestoreComposition : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private RestoreComposition(
        ServiceProvider provider,
        string stateFilePath,
        string installRoot,
        IReadOnlyList<string> credentialRefusedFeeds)
    {
        _provider = provider;
        StateFilePath = stateFilePath;
        InstallRoot = installRoot;
        CredentialRefusedFeeds = credentialRefusedFeeds;
    }

    public string StateFilePath { get; }

    public string InstallRoot { get; }

    public IReadOnlyList<string> CredentialRefusedFeeds { get; }

    public IServiceProvider Services => _provider;

    /// <summary>
    /// Composes the provider and pins its paths. Everything that can refuse — a non-absolute
    /// override, a path that nothing pins, in-memory persistence, invalid options — refuses here,
    /// before any store, feed, or package is touched.
    /// </summary>
    public static RestoreComposition Create(IConfiguration configuration, NuplaneRestoreOptions options)
    {
        // Accepts the host's configuration root or its own Nuplane section — the same value passed
        // to AddNuplane — without changing AddNuplane itself, which still expects to be handed
        // configuration already scoped to Nuplane's own keys.
        var nuplaneConfiguration = RestoreConfigurationResolver.ResolveNuplaneSection(configuration);
        var paths = new RestorePathResolver(options);
        var credentialRefusedFeeds = new List<string>();

        var services = new ServiceCollection();
        services.AddLogging();
        if (options.LoggerFactory is not null)
        {
            services.AddSingleton<ILoggerFactory>(new NonDisposingLoggerFactory(options.LoggerFactory));
        }

        // Loading is deliberately not registered: PackageAutoLoadingObserver is only ever added by
        // Nuplane.Loading's AutoloadPackages, so a composition that never calls it loads nothing.
        services.AddNuplane(nuplaneConfiguration, builder => options.ConfigureBuilder?.Invoke(builder));

        // These post-configure callbacks are registered after AddNuplane, and therefore run after
        // every Configure and PostConfigure the configuration and the builder callback registered —
        // including the directory module's, which adds its feeds through PostConfigure.
        services.PostConfigure<StoreRegistryOptions>(storeOptions =>
        {
            if (storeOptions.UseInMemoryStore)
            {
                throw new InvalidOperationException(
                    "This configuration selects in-memory store persistence, which writes no state file. A restore whose whole purpose is to populate a store would report success and leave nothing behind, so it is refused instead.");
            }

            storeOptions.StateFilePath = paths.ResolveStateFilePath(storeOptions.StateFilePath);
        });

        services.PostConfigure<FeedResolutionOptions>(feedOptions =>
        {
            feedOptions.PackageInstallRoot = paths.ResolveInstallRoot(feedOptions.PackageInstallRoot);
            RefuseCredentialFeeds(feedOptions, credentialRefusedFeeds);
        });

        // Taking IOptions<StoreRegistryOptions> as a dependency is what orders the two: the state
        // file is resolved before the lock file that anchors to its directory.
        services.AddOptions<LockFileOptions>().PostConfigure<IOptions<StoreRegistryOptions>>(
            (lockFileOptions, storeOptions) =>
                lockFileOptions.Path = paths.ResolveLockFilePath(lockFileOptions.Path, storeOptions.Value.StateFilePath!));

        var provider = services.BuildServiceProvider();
        try
        {
            // Materialize every option now so a refusal or a validation failure is thrown from the
            // entry point rather than from somewhere inside the first reconciliation cycle. The
            // store comes first, so "this configuration persists nothing" and "nothing says which
            // store to write" are reported ahead of anything derived from them.
            var persistence = provider.GetRequiredService<EffectiveStorePersistenceSettings>();
            _ = provider.GetRequiredService<IOptions<LockFileOptions>>().Value;
            var feedOptions = provider.GetRequiredService<IOptions<FeedResolutionOptions>>().Value;

            // A feed refused for declaring credentials is a legitimate, already-reported outcome —
            // not "no feeds" — so it does not trip this refusal on its own.
            if (feedOptions.Feeds.Count == 0
                && credentialRefusedFeeds.Count == 0
                && HasNoDesiredPackageSources(provider))
            {
                throw new InvalidOperationException(
                    $"No feed and no desired package source is configured. Pass either the host's configuration root — the one containing a '{RestoreConfigurationResolver.NuplaneSectionName}' section — or that '{RestoreConfigurationResolver.NuplaneSectionName}' section itself, with at least one feed configured under it. "
                    + "A host that genuinely configures no feeds has nothing for a restore to populate, so this is refused rather than reported as an empty success.");
            }

            return new(
                provider,
                // Report the paths the runtime itself derived, not the ones handed to it, so the
                // result cannot drift from where the packages and state actually land.
                persistence.ResolvedStateFilePath!,
                PackageInstallStore.ResolveInstallRoot(feedOptions),
                credentialRefusedFeeds);
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads the desired requests exactly as a reconciliation cycle reads them — the same sources,
    /// with the same disabled-manifest exclusion, through the same aggregator — and classifies each
    /// version constraint. No store, feed acquisition, or network access is involved.
    /// </summary>
    public async Task<NuplaneDesiredDescription> DescribeDesiredAsync(CancellationToken cancellationToken)
    {
        var sources = _provider.GetServices<IDesiredPackageSource>()
            .Where(static source => source is not DesiredManifestPackageSource { IsEnabled: false });

        var aggregate = await _provider.GetRequiredService<IDesiredStateAggregator>()
            .AggregateAsync(sources, cancellationToken)
            .ConfigureAwait(false);

        return new(
            aggregate.Requests.Select(Describe).ToArray(),
            CredentialRefusedFeeds,
            aggregate.SourceErrors.ToDictionary(
                static error => error.Key,
                static error => error.Value.Message,
                StringComparer.Ordinal),
            StateFilePath,
            InstallRoot);
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();

    private static DesiredPackageDescription Describe(PackageRequest request)
    {
        var versionRequest = NuGetVersionRequestClassifier.Classify(request.VersionRange);

        return new(
            request.Id,
            request.VersionRange,
            request.FeedName,
            request.SourceName,
            versionRequest.IsExact,
            versionRequest.IsExact ? versionRequest.ExactVersion : null);
    }

    /// <summary>
    /// Whether no registered <see cref="IDesiredPackageSource"/> would contribute anything — the
    /// same exclusion <see cref="DescribeDesiredAsync"/> and a reconciliation cycle apply, since the
    /// manifest source is always registered but does nothing unless convergence is configured.
    /// </summary>
    private static bool HasNoDesiredPackageSources(IServiceProvider provider) =>
        !provider.GetServices<IDesiredPackageSource>()
            .Any(static source => source is not DesiredManifestPackageSource { IsEnabled: false });

    /// <summary>
    /// Drops every feed that declares credentials, recording its name. Nuplane has no credential
    /// resolver — <c>NuGetRemotePackageAcquirer</c> throws <see cref="NotSupportedException"/> for
    /// any credential — and version enumeration would contact the feed before that throw, so the
    /// refusal has to happen here, before the first network call.
    /// </summary>
    private static void RefuseCredentialFeeds(FeedResolutionOptions feedOptions, List<string> refusedFeeds)
    {
        var credentialFeeds = feedOptions.Feeds
            .Where(static feed => !string.IsNullOrWhiteSpace(feed.Credentials))
            .ToArray();

        foreach (var feed in credentialFeeds)
        {
            feedOptions.Feeds.Remove(feed);
            refusedFeeds.Add(feed.Name);
        }

        refusedFeeds.Sort(StringComparer.OrdinalIgnoreCase);
    }
}
