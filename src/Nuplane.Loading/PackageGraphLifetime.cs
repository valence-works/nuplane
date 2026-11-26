using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using static Nuplane.Loading.PackageProjection;

namespace Nuplane.Loading;

/// <summary>Owns the live graph projection, its exact-session cache, and context retirement.</summary>
internal sealed partial class PackageGraphLifetime(
    PackageAssemblyAssetSelector assetSelector,
    PackageGraphMaterializer materializer,
    HostIntegratedAssemblyResolutionCatalog resolutionCatalog,
    ILogger<PackageLoader> logger,
    bool requiresGraphUseLease)
{
    private readonly HostIntegratedAssemblyResolutionCatalog _resolutionCatalog = resolutionCatalog;
    private readonly ILogger<PackageLoader> _logger = logger;

    // Session-bound readers hold this monitor until their exact lease read pin is released.
    internal object SyncRoot { get; } = new();
    internal bool RequiresGraphUseLease { get; } = requiresGraphUseLease;

    private readonly ConcurrentDictionary<string, AssemblyLoadContext> _contexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PackageLoadSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LoadedGraphCacheEntry> _loadedGraphs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inertPackages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PackageProjection> _loadedPackageProjections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PackageProjection> _inertPackageProjections = new(StringComparer.OrdinalIgnoreCase);

    private long _activationProjectionVersion;
    private StagedLoadBatch? _validatedActivationBatch;

    /// <summary>
    /// Gets the active load sessions keyed by package-version key.
    /// </summary>
    internal IReadOnlyDictionary<string, PackageLoadSession> Sessions => _sessions;

    internal bool IsInertPackage(string packageId, string version) =>
        !string.IsNullOrWhiteSpace(packageId)
        && !string.IsNullOrWhiteSpace(version)
        && _inertPackages.ContainsKey(BuildKey(packageId, version));

    internal bool IsInertPackage(PackageProjection selectedProjection)
    {
        var key = selectedProjection.PackageKey;
        lock (SyncRoot)
        {
            if (!_inertPackageProjections.TryGetValue(key, out var inertProjection)
                || inertProjection != selectedProjection)
                return false;

            // A known inert projection must not hide a still-loaded context for another exact install.
            return !_contexts.ContainsKey(key);
        }
    }

    internal StagedLoadBatch BeginActivationBatch()
    {
        lock (SyncRoot)
            return new(this, _activationProjectionVersion);
    }

    // A validated batch is reserved until the store commit either succeeds or aborts. Capture the
    // live version to detect changes made while package gates and filesystem work were in progress.
    internal void BeginLiveActivationMutation()
    {
        if (_validatedActivationBatch is not null)
            throw new InvalidOperationException("A staged package activation is awaiting its store commit.");

        _activationProjectionVersion++;
    }

    internal static PackageLoadMode ResolveGraphLoadMode(IReadOnlyList<PackageLoadModeSelection> selections) =>
        selections.Any(static selection => selection.LoadMode == PackageLoadMode.HostIntegrated)
            ? PackageLoadMode.HostIntegrated
            : PackageLoadMode.Collectible;

    internal bool TryReuseLoadedGraph(
        IReadOnlyList<ResolvedPackage> packageGraph,
        IReadOnlyList<PackageLoadModeSelection> selections,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken,
        List<PackageLoadSession> loaded,
        Dictionary<string, string> failed,
        IReadOnlyDictionary<string, PackageProjection> packageProjections,
        IReadOnlyDictionary<string, IReadOnlyList<LoadModeDecisionDiagnostic>> diagnosticsByPackageKey,
        PackageGraphUseLeaseOwner? graphLeaseOwner,
        string graphKey,
        PackageLoadMode graphLoadMode,
        bool usesPerPackageContexts)
    {
        lock (SyncRoot)
        {
            if (!TryGetExactGraphSessions(packageGraph, graphKey, graphLoadMode,
                    usesPerPackageContexts, packageProjections, out _))
                return false;

            BeginLiveActivationMutation();
            if (usesPerPackageContexts)
                EnsurePackagesLoaded(packageGraph, sharedPolicy, cancellationToken, loaded, failed,
                    packageProjections, diagnosticsByPackageKey, graphLeaseOwner);
            else
                EnsureGraphLoaded(packageGraph, selections, sharedPolicy, loaded, failed,
                    packageProjections, diagnosticsByPackageKey, graphLeaseOwner);
            return true;
        }
    }

    // Call under SyncRoot to keep the complete cache decision aligned with its contexts.
    internal bool TryGetExactGraphSessions(
        IReadOnlyList<ResolvedPackage> packages,
        string graphKey,
        PackageLoadMode graphLoadMode,
        bool usesPerPackageContexts,
        IReadOnlyDictionary<string, PackageProjection> packageProjections,
        out IReadOnlyList<PackageLoadSession> sessions)
    {
        if (!usesPerPackageContexts)
            return TryGetLoadedGraphSessions(graphKey, graphLoadMode, packageProjections.Values, out sessions);

        var matches = new List<PackageLoadSession>(packages.Count);
        foreach (var package in packages)
        {
            var key = BuildKey(package.Id, package.Version);
            if (!packageProjections.TryGetValue(key, out var projection)
                || !TryGetLoadedSession(key, out var session)
                || !_loadedPackageProjections.TryGetValue(key, out var existingProjection)
                || existingProjection != projection)
            {
                sessions = [];
                return false;
            }

            matches.Add(session);
        }

        sessions = matches;
        return true;
    }

    internal void EnsurePackagesLoaded(
        IReadOnlyList<ResolvedPackage> packages,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken,
        List<PackageLoadSession> loaded,
        Dictionary<string, string> failed,
        IReadOnlyDictionary<string, PackageProjection> packageProjections,
        IReadOnlyDictionary<string, IReadOnlyList<LoadModeDecisionDiagnostic>>? diagnosticsByPackageKey = null,
        PackageGraphUseLeaseOwner? graphLeaseOwner = null)
    {
        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = BuildKey(package.Id, package.Version);
            if (!packageProjections.TryGetValue(key, out var projection))
                throw new InvalidOperationException($"Package projection '{key}' was not captured before loading.");

            if (TryGetLoadedSession(key, out var existing)
                && _loadedPackageProjections.TryGetValue(key, out var existingProjection)
                && existingProjection == projection)
            {
                if (graphLeaseOwner is not null && _contexts.TryGetValue(key, out var existingContext))
                    graphLeaseOwner.TransferToLifetime(existingContext, existingContext.IsCollectible);
                loaded.Add(existing);
                continue;
            }

            PackageGraphLoadContext? newContext = null;
            try
            {
                var selection = assetSelector.Select(package.InstallPath, package.Id);
                var runtimePaths = selection.AssemblyPaths.Where(path => !HostRuntimeAssemblyCatalog.Contains(path)).ToArray();
                if (runtimePaths.Length == 0)
                {
                    var replacedContexts = new List<AssemblyLoadContext>();
                    ReplaceWithInert(projection, replacedContexts);
                    UnloadUnreferencedContexts(replacedContexts);
                    _logger.LogInformation(
                        "Skipped package {PackageId}@{Version} because assembly {AssemblyPath} is provided by the host runtime.",
                        package.Id,
                        package.Version,
                        string.Join(", ", selection.AssemblyPaths));
                    continue;
                }

                var graphPackages = new LoadableGraphPackage[] { new(package.Id, package.Version, package.InstallPath, selection) };
                newContext = materializer.CreateContext(key, PackageLoadMode.Collectible, graphPackages,
                    [package.InstallPath], sharedPolicy, graphLeaseOwner);
                PackageGraphMaterializer.LoadAssemblies(newContext, graphPackages);

                var replaced = new List<AssemblyLoadContext>();
                if (_contexts.TryGetValue(key, out var previousContext) && !ReferenceEquals(previousContext, newContext))
                    replaced.Add(previousContext);
                _contexts[key] = newContext;
                _loadedPackageProjections[key] = projection;
                ClearInert(key);

                var session = new PackageLoadSession(
                    package.Id,
                    package.Version,
                    package.InstallPath,
                    key,
                    DateTimeOffset.UtcNow,
                    IsLoaded: true,
                    LastError: null,
                    PackageLoadMode.Collectible,
                    FrameworkIntegrationSafe: false,
                    LoadModeDiagnostics: ResolveLoadModeDiagnostics(diagnosticsByPackageKey, key));

                _sessions[key] = session;
                loaded.Add(session);
                RemoveKeyFromLoadedGraphs(key);
                UnloadUnreferencedContexts(replaced);
            }
            catch (Exception ex)
            {
                // A scoped owner is transferred by the context constructor. If materialization fails before
                // the context is published, request its normal collectible unload so that owner can drain.
                // The previously published context (if any) remains mapped and retained.
                newContext?.Unload();
                RecordPackageFailure(
                    package.Id,
                    package.Version,
                    package.InstallPath,
                    key,
                    PackageLoadMode.Collectible,
                    ex.Message,
                    failed,
                    diagnosticsByPackageKey);
            }
        }
    }

    internal void EnsureGraphLoaded(
        IReadOnlyList<ResolvedPackage> packages,
        IReadOnlyList<PackageLoadModeSelection> selections,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        List<PackageLoadSession> loaded,
        Dictionary<string, string> failed,
        IReadOnlyDictionary<string, PackageProjection> packageProjections,
        IReadOnlyDictionary<string, IReadOnlyList<LoadModeDecisionDiagnostic>> diagnosticsByPackageKey,
        PackageGraphUseLeaseOwner? graphLeaseOwner = null)
    {
        var graphKey = BuildGraphKey(packages);
        var graphLoadMode = ResolveGraphLoadMode(selections);

        if (TryGetLoadedGraphSessions(graphKey, graphLoadMode, packageProjections.Values, out var existingGraphSessions))
        {
            if (graphLeaseOwner is not null)
            {
                var existingContext = existingGraphSessions
                    .Select(session => _contexts.TryGetValue(BuildKey(session.PackageId, session.Version), out var context) ? context : null)
                    .FirstOrDefault(static context => context is not null);
                if (existingContext is not null)
                    graphLeaseOwner.TransferToLifetime(existingContext, existingContext.IsCollectible);
            }
            loaded.AddRange(existingGraphSessions);
            return;
        }

        AssemblyLoadContext? context = null;
        var replacedContexts = new List<AssemblyLoadContext>();
        var catalogPublished = false;
        GraphPackageResolution? graphPackages = null;

        try
        {
            graphPackages = materializer.ResolvePackages(packages);
            graphPackages.ThrowIfUnusable(graphKey);
            if (graphPackages.LoadablePackages.Count == 0)
            {
                PublishInertMembers(graphPackages, packageProjections, replacedContexts);
                UnloadUnreferencedContexts(replacedContexts);
                return;
            }

            if (graphLoadMode == PackageLoadMode.HostIntegrated)
            {
                var candidates = materializer.BuildResolutionCandidates(graphKey, graphPackages.LoadablePackages, sharedPolicy);
                _resolutionCatalog.ValidateCanPublishGraph(graphKey, candidates);
            }

            context = materializer.CreateContext(graphKey, graphLoadMode, graphPackages.LoadablePackages,
                packages.Select(static package => package.InstallPath).ToArray(), sharedPolicy, graphLeaseOwner);
            PackageGraphMaterializer.LoadAssemblies(context, graphPackages.LoadablePackages);

            if (graphLoadMode == PackageLoadMode.HostIntegrated)
            {
                var assemblies = materializer.GetHostIntegratedAssemblies(context, graphPackages.LoadablePackages, sharedPolicy);
                _resolutionCatalog.PublishGraph(graphKey, selections, assemblies);
                catalogPublished = true;
            }

            PublishLoadedGraph(graphPackages, graphKey, graphLoadMode, context, packageProjections,
                diagnosticsByPackageKey, loaded, replacedContexts);
            UnloadUnreferencedContexts(replacedContexts);
        }
        catch (Exception exception)
        {
            RollBackFailedGraph(packages, graphPackages, graphKey, graphLoadMode, context, catalogPublished,
                exception.Message, failed, diagnosticsByPackageKey);
        }
    }

    private void PublishLoadedGraph(
        GraphPackageResolution graphPackages,
        string graphKey,
        PackageLoadMode graphLoadMode,
        AssemblyLoadContext context,
        IReadOnlyDictionary<string, PackageProjection> packageProjections,
        IReadOnlyDictionary<string, IReadOnlyList<LoadModeDecisionDiagnostic>> diagnosticsByPackageKey,
        List<PackageLoadSession> loaded,
        List<AssemblyLoadContext> replacedContexts)
    {
        foreach (var package in graphPackages.LoadablePackages)
        {
            var key = BuildKey(package.Id, package.Version);
            if (_contexts.TryGetValue(key, out var previousContext) &&
                !ReferenceEquals(previousContext, context))
            {
                replacedContexts.Add(previousContext);
            }

            _contexts[key] = context;
            if (!packageProjections.TryGetValue(key, out var projection))
                throw new InvalidOperationException($"Package projection '{key}' was not captured before loading.");
            _loadedPackageProjections[key] = projection;
            ClearInert(key);
            RemoveKeyFromLoadedGraphs(key);

            var session = new PackageLoadSession(
                package.Id,
                package.Version,
                package.InstallPath,
                graphKey,
                DateTimeOffset.UtcNow,
                IsLoaded: true,
                LastError: null,
                graphLoadMode,
                FrameworkIntegrationSafe: graphLoadMode == PackageLoadMode.HostIntegrated,
                LoadModeDiagnostics: ResolveLoadModeDiagnostics(diagnosticsByPackageKey, key));

            _sessions[key] = session;
            loaded.Add(session);

            _logger.LogInformation(
                "Loaded package {PackageId}@{Version} with LoadMode={LoadMode} ContextKey={ContextKey}.",
                package.Id,
                package.Version,
                graphLoadMode,
                graphKey);
        }

        PublishInertMembers(graphPackages, packageProjections, replacedContexts);
        _loadedGraphs[graphKey] = new(
            graphLoadMode,
            graphPackages.LoadablePackages
                .Select(static package => BuildKey(package.Id, package.Version))
                .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            OrderProjections(packageProjections.Values));
    }

    private void PublishInertMembers(
        GraphPackageResolution graphPackages,
        IReadOnlyDictionary<string, PackageProjection> packageProjections,
        List<AssemblyLoadContext> replacedContexts)
    {
        foreach (var inertPackage in graphPackages.InertPackages)
        {
            var key = BuildKey(inertPackage.Id, inertPackage.Version);
            if (!packageProjections.TryGetValue(key, out var projection))
                throw new InvalidOperationException($"Package projection '{key}' was not captured before loading.");
            ReplaceWithInert(projection, replacedContexts);
        }
    }

    private void RollBackFailedGraph(
        IReadOnlyList<ResolvedPackage> packages,
        GraphPackageResolution? graphPackages,
        string graphKey,
        PackageLoadMode graphLoadMode,
        AssemblyLoadContext? context,
        bool catalogPublished,
        string message,
        Dictionary<string, string> failed,
        IReadOnlyDictionary<string, IReadOnlyList<LoadModeDecisionDiagnostic>> diagnosticsByPackageKey)
    {
        if (context?.IsCollectible == true)
        {
            context.Unload();
        }

        foreach (var package in ResolveFailedGraphPackages(packages, graphPackages))
        {
            var key = BuildKey(package.Id, package.Version);
            if (context is not null &&
                _contexts.TryGetValue(key, out var existingContext) &&
                ReferenceEquals(existingContext, context))
            {
                _contexts.TryRemove(key, out _);
            }

            if (catalogPublished)
            {
                _resolutionCatalog.RemovePackage(package.Id, package.Version);
            }

            RecordPackageFailure(
                package.Id,
                package.Version,
                package.InstallPath,
                graphKey,
                graphLoadMode,
                message,
                failed,
                diagnosticsByPackageKey);
        }
    }

    /// <summary>
    /// The single definition of "this package version is already loaded", used both by the per-package load
    /// path and by the activation-gate pre-check.
    /// </summary>
    private bool TryGetLoadedSession(string packageKey, [NotNullWhen(true)] out PackageLoadSession? session)
    {
        session = _sessions.TryGetValue(packageKey, out var existing) && existing.IsLoaded
            ? existing
            : null;

        return session is not null;
    }

    /// <summary>
    /// The single definition of "this graph generation is already loaded in this mode", used both by the
    /// graph load path and by the activation-gate pre-check.
    /// </summary>
    private bool TryGetLoadedGraphSessions(
        string graphKey,
        PackageLoadMode graphLoadMode,
        IEnumerable<PackageProjection> packageProjections,
        out IReadOnlyList<PackageLoadSession> existingGraphSessions)
    {
        existingGraphSessions = [];

        if (!_loadedGraphs.TryGetValue(graphKey, out var cachedGraph) || cachedGraph.LoadMode != graphLoadMode)
        {
            return false;
        }

        if (!cachedGraph.Projections.SequenceEqual(OrderProjections(packageProjections)))
            return false;

        var sessions = new List<PackageLoadSession>(cachedGraph.LoadablePackageKeys.Count);
        foreach (var key in cachedGraph.LoadablePackageKeys)
        {
            if (!TryGetLoadedSession(key, out var session)
                || !string.Equals(session.ContextKey, graphKey, StringComparison.OrdinalIgnoreCase)
                || session.LoadMode != graphLoadMode)
            {
                return false;
            }

            sessions.Add(session);
        }

        existingGraphSessions = sessions;
        return true;
    }

    /// <summary>
    /// The single definition of how a package that could not be loaded is recorded: the failure reason for the
    /// caller, a non-loaded session carrying the same reason, and a cleared inert marker so the package stays
    /// retryable. Every load failure and every activation block goes through here.
    /// </summary>
    private void RecordPackageFailure(
        string packageId,
        string version,
        string installPath,
        string contextKey,
        PackageLoadMode loadMode,
        string message,
        Dictionary<string, string> failed,
        IReadOnlyDictionary<string, IReadOnlyList<LoadModeDecisionDiagnostic>>? diagnosticsByPackageKey)
    {
        var key = BuildKey(packageId, version);

        // A package reported as failed must stay retryable, so it can never remain marked inert.
        ClearInert(key);

        failed[packageId] = message;
        _sessions[key] = new(
            packageId,
            version,
            installPath,
            contextKey,
            DateTimeOffset.UtcNow,
            IsLoaded: false,
            LastError: message,
            loadMode,
            FrameworkIntegrationSafe: false,
            LoadModeDiagnostics: ResolveLoadModeDiagnostics(diagnosticsByPackageKey, key));
    }

    // Fails every member of a graph that was refused before loading started: there is no load context, no
    // published catalog entry, and no cached graph generation to unwind, so only the per-package failure
    // bookkeeping applies — the same bookkeeping the load paths use when they fail.
    internal void RecordGraphFailure(
        IReadOnlyList<ResolvedPackage> packages,
        string graphKey,
        PackageLoadMode graphLoadMode,
        bool usesPerPackageContexts,
        string message,
        Dictionary<string, string> failed,
        IReadOnlyDictionary<string, IReadOnlyList<LoadModeDecisionDiagnostic>> diagnosticsByPackageKey)
    {
        foreach (var package in packages)
        {
            RecordPackageFailure(
                package.Id,
                package.Version,
                package.InstallPath,
                usesPerPackageContexts ? BuildKey(package.Id, package.Version) : graphKey,
                usesPerPackageContexts ? PackageLoadMode.Collectible : graphLoadMode,
                message,
                failed,
                diagnosticsByPackageKey);
        }
    }

    private void ReplaceWithInert(PackageProjection projection, List<AssemblyLoadContext> replacedContexts)
    {
        var key = projection.PackageKey;
        if (_contexts.TryRemove(key, out var previousContext))
            replacedContexts.Add(previousContext);
        _loadedPackageProjections.TryRemove(key, out _);
        _sessions.TryRemove(key, out _);
        RemoveKeyFromLoadedGraphs(key);
        if (TrySplitKey(key, out var packageId, out var version))
            _resolutionCatalog.RemovePackage(packageId, version);
        MarkInert(projection);
    }

    private void MarkInert(PackageProjection projection)
    {
        var packageKey = projection.PackageKey;
        _inertPackages[packageKey] = 0;
        _inertPackageProjections[packageKey] = projection;

        // A package the loader has just evaluated as an inert graph member has no outstanding failure. Drop a
        // failed session left by an earlier attempt — for example a graph an activation gate refused before it
        // could be resolved — so read surfaces report the package as skipped, exactly as they would had the
        // earlier attempt never happened.
        if (_sessions.TryGetValue(packageKey, out var session) && !session.IsLoaded)
        {
            _sessions.TryRemove(new KeyValuePair<string, PackageLoadSession>(packageKey, session));
        }
    }

    private void ClearInert(string packageKey)
    {
        _inertPackages.TryRemove(packageKey, out _);
        _inertPackageProjections.TryRemove(packageKey, out _);
    }

    internal static IReadOnlyList<PackageProjection> OrderProjections(IEnumerable<PackageProjection> projections) =>
        projections.OrderBy(static projection => projection.PackageKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static projection => projection.PackageKey, StringComparer.Ordinal)
            .ToArray();

    private void UnloadUnreferencedContexts(IEnumerable<AssemblyLoadContext> contexts)
    {
        foreach (var context in contexts.Distinct())
        {
            if (context.IsCollectible && !_contexts.Values.Any(existing => ReferenceEquals(existing, context)))
            {
                context.Unload();
            }
        }
    }

    internal IReadOnlyList<string> UnloadContextsNotActive(IReadOnlyDictionary<string, string> activeVersionById)
    {
        ArgumentNullException.ThrowIfNull(activeVersionById);

        lock (SyncRoot)
        {

            var activeKeys = new HashSet<string>(
                activeVersionById.Select(static entry => BuildKey(entry.Key, entry.Value)),
                StringComparer.OrdinalIgnoreCase);

            // Snapshot the keys before mutating: only currently-loaded contexts whose id@version is no longer
            // the active identity are eligible. Everything still active is left completely untouched.
            var inactiveKeys = _contexts.Keys
                .Where(key => !activeKeys.Contains(key))
                .ToArray();

            if (inactiveKeys.Length == 0)
            {
                return [];
            }

            BeginLiveActivationMutation();
            var removedContexts = new List<AssemblyLoadContext>();
            foreach (var key in inactiveKeys)
            {
                _sessions.TryRemove(key, out _);
                ClearInert(key);
                _loadedPackageProjections.TryRemove(key, out _);
                RemoveKeyFromLoadedGraphs(key);

                if (_contexts.TryRemove(key, out var context))
                {
                    removedContexts.Add(context);
                }

                if (TrySplitKey(key, out var packageId, out var version))
                {
                    _resolutionCatalog.RemovePackage(packageId, version);
                }
            }

            // Only actually unloads a context once no *remaining* (still-active) key references it, so a graph
            // context shared with a package that is still active is preserved. Non-collectible contexts are skipped.
            UnloadUnreferencedContexts(removedContexts);

            foreach (var key in inactiveKeys)
            {
                _logger.LogInformation("Unloaded package context {ContextKey} because it is no longer active.", key);
            }

            return inactiveKeys;
        }
    }

    // Drops any cached loaded-graph entry that included this package key, so a later reactivation of the
    // package rebuilds its graph from scratch instead of matching a now-partial cache entry.
    private void RemoveKeyFromLoadedGraphs(string key)
    {
        foreach (var graphKey in _loadedGraphs
            .Where(entry => entry.Value.Projections.Any(projection =>
                string.Equals(projection.PackageKey, key, StringComparison.OrdinalIgnoreCase)))
            .Select(static entry => entry.Key)
            .ToArray())
        {
            _loadedGraphs.TryRemove(graphKey, out _);
        }
    }

    // Context keys are BuildKey output: "{packageId}@{version}". Package ids and semver versions never
    // contain '@', so the first '@' is an unambiguous separator.
    private static bool TrySplitKey(string key, out string packageId, out string version)
    {
        var separatorIndex = key.IndexOf('@');
        if (separatorIndex <= 0 || separatorIndex >= key.Length - 1)
        {
            packageId = string.Empty;
            version = string.Empty;
            return false;
        }

        packageId = key[..separatorIndex];
        version = key[(separatorIndex + 1)..];
        return true;
    }

    internal bool TryRemoveContext(string packageId, string version, out PackageLoadContextHandle? context)
    {
        lock (SyncRoot)
        {
            BeginLiveActivationMutation();
            var key = BuildKey(packageId, version);
            _sessions.TryRemove(key, out _);
            ClearInert(key);
            _loadedPackageProjections.TryRemove(key, out _);
            RemoveKeyFromLoadedGraphs(key);

            if (_contexts.TryRemove(key, out var removed))
            {
                context = new(key, removed);
                _resolutionCatalog.RemovePackage(packageId, version);
                return true;
            }

            context = null;
            return false;
        }
    }

    internal bool TryGetContext(string packageId, string version, out PackageLoadContextHandle? context)
    {
        var key = BuildKey(packageId, version);
        if (_contexts.TryGetValue(key, out var existing))
        {
            context = new(key, existing);
            return true;
        }

        context = null;
        return false;
    }

    private static IReadOnlyList<GraphPackage> ResolveFailedGraphPackages(
        IReadOnlyList<ResolvedPackage> packages,
        GraphPackageResolution? graphPackages)
    {
        if (graphPackages is null)
        {
            return packages
                .Select(static package => new GraphPackage(package.Id, package.Version, package.InstallPath))
                .ToArray();
        }

        if (graphPackages.LoadablePackages.Count == 0 && graphPackages.FailedPackages.Count == 0)
        {
            return graphPackages.SkippedPackages;
        }

        return graphPackages.LoadablePackages
            .Select(static package => new GraphPackage(package.Id, package.Version, package.InstallPath))
            .Concat(graphPackages.FailedPackages)
            .GroupBy(static package => BuildKey(package.Id, package.Version), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
    }

    internal static IReadOnlyList<LoadModeDecisionDiagnostic> ResolveLoadModeDiagnostics(
        IReadOnlyDictionary<string, IReadOnlyList<LoadModeDecisionDiagnostic>>? diagnosticsByPackageKey,
        string packageKey) =>
        diagnosticsByPackageKey is not null && diagnosticsByPackageKey.TryGetValue(packageKey, out var diagnostics)
            ? diagnostics
            : [];

    internal PackageGraphUseLease? GetGraphUseLease(AssemblyLoadContext context)
    {
        var lease = (context as IGraphUseLeaseBoundLoadContext)?.GraphUseLease;
        if (lease is null)
            RefuseUnscopedRead();
        return lease;
    }

    internal PackageGraphUseLease GetGraphUseLeaseUnderLock(ActivePackage package, PackageLoadSession session)
    {
        var key = BuildKey(package.PackageId, package.Version);
        if (!_sessions.TryGetValue(key, out var currentSession) || !ReferenceEquals(currentSession, session) ||
            !session.IsLoaded)
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.ExpiredScope,
                "The active package no longer has its exact current load session.");
        }

        if (!string.Equals(session.PackageId, package.PackageId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(session.Version, package.Version, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(session.ActiveInstallPath, package.InstallPath, StringComparison.Ordinal))
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                "The active package does not match its exact loaded package session.");
        }

        if (!_contexts.TryGetValue(key, out var context) ||
            !_loadedPackageProjections.TryGetValue(key, out var projection))
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.ExpiredScope,
                "The active package's load context or native install projection has expired.");
        }

        if (!string.Equals(projection.PackageKey, key, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(projection.InstallPath, package.InstallPath, StringComparison.Ordinal) ||
            projection.NativeIdentity is null)
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                "The active package does not match its captured native install projection.",
                projection.NativeIdentity?.Root);
        }

        var lease = GetGraphUseLease(context)
            ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "The current participant load context has no graph-use lease.");
        var identity = lease.GetInstallIdentityForExactPath(session.ActiveInstallPath);
        if (identity != projection.NativeIdentity ||
            !string.Equals(identity.PackageId, package.PackageId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(identity.Version, package.Version, StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                "The active package and captured graph-use identity do not match.", identity.Root);
        }

        return lease;
    }

    internal void RefuseUnscopedRead()
    {
        if (RequiresGraphUseLease)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "Participating package loading requires a published graph-use lease.");
    }

    internal IReadOnlyDictionary<string, string> FindProspectiveCandidateConflicts(
        IReadOnlySet<string> retainedKeys,
        IReadOnlyList<HostIntegratedAssemblyResolutionCandidate> candidates) =>
        _resolutionCatalog.FindProspectiveCandidateConflicts(retainedKeys, candidates);
}

internal sealed record LoadedGraphCacheEntry(
    PackageLoadMode LoadMode,
    IReadOnlyList<string> LoadablePackageKeys,
    IReadOnlyList<PackageProjection> Projections);

internal sealed record PackageProjection(
    string PackageKey,
    string InstallPath,
    string? PackageContentHash,
    PackageInstallIdentity? NativeIdentity)
{
    internal static string BuildKey(string packageId, string version) => $"{packageId}@{version}";

    /// <summary>
    /// The single definition of the deterministic load-context key of a package graph. It is stable for
    /// the same set of package identities regardless of which code path assembles the graph, which is what
    /// lets a caller outside this class recognise a graph that is already loaded into this process.
    /// </summary>
    internal static string BuildGraphKey(IReadOnlyList<ResolvedPackage> packages) =>
        "graph:" + string.Join('|', packages
            .OrderBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static package => package.Version, StringComparer.OrdinalIgnoreCase)
            .Select(static package => BuildKey(package.Id, package.Version)));
}
