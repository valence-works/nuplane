using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Capabilities;
using Nuplane.Feeds;
using Nuplane.Feeds.Configuration;
using Nuplane.Feeds.Credentials;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Reconciliation.LockFile;
using Nuplane.Reconciliation.Models;
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
    /// The name of the section a configuration root nests Nuplane's own configuration under.
    /// </summary>
    internal const string NuplaneSectionName = "Nuplane";

    /// <summary>
    /// Composes the provider and pins its paths. Everything that can refuse — a non-absolute
    /// override, a path that nothing pins, in-memory persistence, invalid options, an empty
    /// composition — refuses here, before any store, feed, or package is touched.
    /// </summary>
    public static async Task<RestoreComposition> Create(IConfiguration configuration, NuplaneRestoreOptions options)
    {
        // Accepts the host's configuration root or its own Nuplane section — the same value passed
        // to AddNuplane — without changing AddNuplane itself, which still expects to be handed
        // configuration already scoped to Nuplane's own keys.
        var nuplaneConfiguration = ResolveNuplaneSection(configuration);
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
        // The callback is handed the already-resolved Nuplane configuration, not the value the
        // caller passed in, so a closure that captured the caller's unresolved root — the trap
        // NuplaneRestoreOptions.ConfigureBuilder's docs warn about — is never the reason a module
        // registration helper finds nothing.
        //
        // NuplaneBuilder.BasePath is set before the callback runs, not after, so a module
        // registration helper the callback calls — AddDirectoryFeedsFromConfiguration is the current
        // one — resolves its own relative configured paths against the host's BasePath without the
        // callback having to forward it itself. Setting it first rather than last also means a
        // callback that calls NuplaneBuilder.UseBasePath itself overrides this restore's base
        // instead of being silently overridden by it.
        services.AddNuplane(nuplaneConfiguration, builder =>
        {
            builder.BasePath = paths.BasePath;
            options.ConfigureBuilder?.Invoke(builder, nuplaneConfiguration);
        });

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
            feedOptions.PackageInstallRoot = paths.ResolveInstallRoot(feedOptions.PackageInstallRoot));

        // A desired request that names more than one version is refused by RestoreAsync itself,
        // before the cycle. A contribution cannot be: the package that declares it must be acquired
        // before its nuplane.json can be read. The demand therefore travels into the cycle, where it
        // is honoured before the contributed package is resolved or downloaded. Only ever switched
        // on here, so a configured Reconciliation section that set it for its own reasons is never
        // switched off by a restore that does not demand pins.
        if (options.RequirePinnedVersions)
        {
            services.PostConfigure<ReconciliationOptions>(reconciliationOptions =>
                reconciliationOptions.RequirePinnedContributions = true);
        }

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

            // Built here even when no feed references a secret, so that an ambiguous provider
            // registration — two providers claiming one provider name — refuses the restore while it
            // is still composing, rather than at whichever later moment something first needed a
            // secret. Nothing has been written or fetched at this point.
            var secretReferenceResolver = provider.GetRequiredService<ISecretReferenceResolver>();

            // After validation, and before anything reads the feed list: a feed whose secret
            // reference resolves stays and is used authenticated; one that cannot be resolved is
            // dropped and named here, before the first network call.
            await RefuseUnresolvableCredentialFeedsAsync(provider, secretReferenceResolver, feedOptions, credentialRefusedFeeds)
                .ConfigureAwait(false);

            // A feed refused for declaring credentials is a legitimate, already-reported outcome —
            // not "no feeds" — so it does not trip this refusal on its own.
            if (feedOptions.Feeds.Count == 0
                && credentialRefusedFeeds.Count == 0
                && HasNoDesiredPackageSources(provider))
            {
                throw new InvalidOperationException(
                    $"No feed and no desired package source is configured. Pass either the host's configuration root — the one containing a '{NuplaneSectionName}' section — or that '{NuplaneSectionName}' section itself, with at least one feed configured under it. "
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
            // Async disposal throughout, matching the DisposeAsync a successfully composed instance
            // is disposed through: the container may hold a service that is only IAsyncDisposable, for
            // which the synchronous Dispose() a catch block reaches for by habit would throw.
            await provider.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Returns <paramref name="configuration"/>'s own <see cref="NuplaneSectionName"/> child section
    /// when it exists, and <paramref name="configuration"/> itself otherwise — so a configuration
    /// root and the <c>Nuplane</c> section it nests both resolve to the same effective configuration.
    /// </summary>
    private static IConfiguration ResolveNuplaneSection(IConfiguration configuration)
    {
        var section = configuration.GetSection(NuplaneSectionName);
        return section.Exists() ? section : configuration;
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
            InstallRoot,
            new Dictionary<string, CapabilitySelection>(
                _provider.GetRequiredService<IOptions<CapabilityOptions>>().Value.Selections,
                StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The capability contributions the cycle this composition just ran refused for not naming a
    /// single version, described the same way an unpinned desired request is. Empty unless
    /// <see cref="NuplaneRestoreOptions.RequirePinnedVersions"/> refused one, which is why a restore
    /// that acquired its roots reports these on a degraded result rather than as a skip: the roots
    /// were acquired, only the contributions were refused.
    /// </summary>
    public IReadOnlyList<DesiredPackageDescription> DescribeUnpinnedContributions() =>
        _provider.GetRequiredService<CapabilityContributionLedger>()
            .UnpinnedRequests
            .Select(Describe)
            .ToArray();

    /// <summary>
    /// The stage and message the cycle this composition just ran recorded for each package it
    /// failed, read from the same store registry the cycle's failure recorder wrote through — the
    /// join a caller would otherwise have to do itself between <c>FailedPackages</c> and the state
    /// file's <c>LastFailureById</c>.
    /// </summary>
    /// <remarks>
    /// <c>LastFailureById</c> is a last-known record per package, not a log of this run, so the join
    /// is on the cycle's own failed ids <i>and</i> its correlation id: a record an earlier cycle left
    /// for a package this one failed for a reason it did not record — a graph node failed only
    /// because a sibling was — is left out rather than reported as if it had just happened.
    /// </remarks>
    public async Task<IReadOnlyList<PackageRefusal>> DescribeRefusalsAsync(ReconciliationRunResult run, CancellationToken cancellationToken)
    {
        if (run.FailedPackages.Count == 0)
        {
            return [];
        }

        var state = await _provider.GetRequiredService<IStoreRegistry>().GetStateAsync(cancellationToken).ConfigureAwait(false);

        return run.FailedPackages
            .Select(state.LastFailureById.GetValueOrDefault)
            .OfType<FailureRecord>()
            .Where(record => string.Equals(record.CorrelationId, run.ChangeSet.CorrelationId, StringComparison.Ordinal))
            .OrderBy(static record => record.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(static record => new PackageRefusal(record.PackageId, record.Stage, record.Message))
            .ToArray();
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
    /// Drops every feed whose configured secret reference no registered
    /// <see cref="ISecretReferenceProvider"/> could resolve, recording its name. A feed whose
    /// reference does resolve is left in place and contacted with credentials, like any other feed.
    /// </summary>
    /// <remarks>
    /// This runs while composing, rather than during the cycle, because version enumeration would
    /// otherwise contact the feed before the acquirer could refuse it. A provider that throws —
    /// an unreachable secret store, say — refuses the feed too: a restore reports its failures
    /// rather than throwing them, and a feed whose secret could not be read is exactly the case
    /// <see cref="CredentialRefusedFeeds"/> exists to report.
    /// </remarks>
    private static async Task RefuseUnresolvableCredentialFeedsAsync(
        IServiceProvider provider,
        ISecretReferenceResolver resolver,
        FeedResolutionOptions feedOptions,
        List<string> refusedFeeds)
    {
        var credentialFeeds = feedOptions.Feeds
            .Where(static feed => !string.IsNullOrWhiteSpace(feed.Credentials))
            .ToArray();

        if (credentialFeeds.Length == 0)
        {
            return;
        }

        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger<RestoreComposition>();

        foreach (var feed in credentialFeeds)
        {
            FeedCredentialLookup lookup;
            try
            {
                lookup = await FeedCredentials.ResolveAsync(resolver, feed, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Resolving the configured credentials of feed {FeedName} failed, so the feed is refused for this restore.",
                    feed.Name);
                lookup = FeedCredentialLookup.Refused;
            }

            if (!lookup.IsRefused)
            {
                continue;
            }

            feedOptions.Feeds.Remove(feed);
            refusedFeeds.Add(feed.Name);
        }

        refusedFeeds.Sort(StringComparer.OrdinalIgnoreCase);
    }
}
