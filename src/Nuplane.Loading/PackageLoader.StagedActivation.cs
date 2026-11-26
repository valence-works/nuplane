using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Reconciliation;

namespace Nuplane.Loading;

internal sealed partial class PackageLoader
{
    private long _activationProjectionVersion;
    private OrdinaryLoadBatch? _validatedActivationBatch;

    internal async Task<OrdinaryLoadBatch> StageOrdinaryGraphsAsync(
        IReadOnlyList<IReadOnlyList<ResolvedPackage>> graphs,
        IReadOnlyDictionary<string, string> prospectiveActiveVersions,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graphs);
        ArgumentNullException.ThrowIfNull(prospectiveActiveVersions);
        ArgumentNullException.ThrowIfNull(sharedPolicy);
        RefuseUnscopedRead();

        var packages = graphs.Select(static graph => graph.ToArray()).ToArray();
        var policy = sharedPolicy.ToArray();
        var targetKeys = GetActiveKeys(prospectiveActiveVersions);
        var batch = BeginActivationBatch();
        try
        {
            var plans = new List<StagedGraphPlan>();
            foreach (var graph in packages)
            {
                var plan = await PlanGraphAsync(batch, graph, CreatePackageProjections(graph), policy, null, cancellationToken)
                    .ConfigureAwait(false);
                if (plan is not null)
                    plans.Add(plan);
            }

            StagePlannedGraphs(batch, plans, targetKeys, policy, cancellationToken);
            return batch;
        }
        catch
        {
            batch.Dispose();
            throw;
        }
    }

    internal async Task<OrdinaryLoadBatch> StageLeasedGraphsAsync(
        IReadOnlyList<ScopedResolvedPackageGraph> graphs,
        IReadOnlyDictionary<string, string> prospectiveActiveVersions,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graphs);
        ArgumentNullException.ThrowIfNull(prospectiveActiveVersions);
        ArgumentNullException.ThrowIfNull(sharedPolicy);

        var work = graphs.Select(graph => graph is null
            ? null
            : new LeasedGraphWorkItem(graph.Packages, graph.LeaseOwner,
                () => ScopedResolvedPackageGraphValidator.Validate(graph))).ToArray();
        var policy = sharedPolicy.ToArray();
        var targetKeys = GetActiveKeys(prospectiveActiveVersions);
        var readPins = new List<IDisposable>();
        var batch = BeginActivationBatch();
        try
        {
            try
            {
                if (work.Any(static graph => graph is null) ||
                    work.Select(static graph => graph!.LeaseOwner).Distinct().Count() != work.Length)
                {
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                        "Each selected graph must have a distinct published lease owner.");
                }

                PrepareLeasedGraphReads(work, readPins);
                var plans = new List<StagedGraphPlan>();
                foreach (var graph in work)
                {
                    var selected = graph!;
                    var plan = await PlanGraphAsync(batch, selected.Packages, selected.PackageProjections!, policy,
                        selected.LeaseOwner, cancellationToken).ConfigureAwait(false);
                    if (plan is not null)
                        plans.Add(plan);
                }

                StagePlannedGraphs(batch, plans, targetKeys, policy, cancellationToken);
            }
            finally
            {
                // The coordinator owns pending leases through its authority check. A materialized
                // context has already taken ownership of its lease from the coordinator.
                for (var index = readPins.Count - 1; index >= 0; index--)
                    readPins[index].Dispose();
            }

            return batch;
        }
        catch
        {
            batch.Dispose();
            throw;
        }
    }

    private OrdinaryLoadBatch BeginActivationBatch()
    {
        lock (_loadContextSync)
            return new(this, _activationProjectionVersion);
    }

    // A validated batch is reserved until the store commit either succeeds or aborts. Capture the
    // live version to detect changes made while package gates and filesystem work were in progress.
    private void BeginLiveActivationMutation()
    {
        if (_validatedActivationBatch is not null)
            throw new InvalidOperationException("A staged package activation is awaiting its store commit.");

        _activationProjectionVersion++;
    }

    private static HashSet<string> GetActiveKeys(IReadOnlyDictionary<string, string> versions) =>
        versions.Select(static version => BuildKey(version.Key, version.Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async Task<StagedGraphPlan?> PlanGraphAsync(
        OrdinaryLoadBatch batch,
        IReadOnlyList<ResolvedPackage> packages,
        IReadOnlyDictionary<string, PackageProjection> projections,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        PackageGraphUseLeaseOwner? leaseOwner,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (packages.Count == 0)
            return null;

        var graphKey = BuildGraphKey(packages);
        var lease = leaseOwner?.Lease;
        IReadOnlyList<PackageGraphUseLease>? leases = lease is null ? null : [lease];
        using var readCheckpoint = lease is null ? null : PackageAssemblyReadCheckpoint.EnterGraph(lease.Snapshot);
        if (lease is not null)
            PackageAssemblyReadCheckpoint.BeforeLoadModeSelection();
        var decision = await _loadModeSelector.SelectGraphAsync(
            packages, _options, graphKey, cancellationToken, leases).ConfigureAwait(false);
        var mode = ResolveGraphLoadMode(decision.Selections);
        var usesPerPackageContext = UsesPerPackageContexts(packages, decision.Selections);
        lock (_loadContextSync)
        {
            if (IsGraphAlreadyLoadedUnderLock(packages, graphKey, mode, usesPerPackageContext, projections))
            {
                if (usesPerPackageContext)
                    batch.Loaded.Add(_sessions[BuildKey(packages[0].Id, packages[0].Version)]);
                else if (TryGetLoadedGraphSessions(graphKey, mode, projections.Values, out var sessions))
                    batch.Loaded.AddRange(sessions);
                return null;
            }
        }

        var block = await EvaluateActivationGatesAsync(packages, graphKey, mode, cancellationToken, leases)
            .ConfigureAwait(false);
        if (block is not null)
        {
            batch.RecordFailure(packages, block);
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var resolved = ResolveGraphPackages(packages);
            if (resolved.ResolutionFailure is not null)
                throw resolved.ResolutionFailure;

            if (resolved.LoadablePackages.Count == 0 && resolved.SkippedPackages.Count != 0)
            {
                throw new NoLoadableAssemblyException(
                    $"No loadable assembly found in graph '{graphKey}'. Scanned install paths: {FormatQuotedList(resolved.SkippedInstallPaths)}.");
            }

            var candidates = mode == PackageLoadMode.HostIntegrated
                ? BuildHostIntegratedResolutionCandidates(graphKey, resolved.LoadablePackages, sharedPolicy)
                : [];
            return new(packages, projections, graphKey, mode, usesPerPackageContext,
                resolved, decision, candidates, leaseOwner);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            batch.RecordFailure(packages, exception.Message);
            return null;
        }
    }

    private void StagePlannedGraphs(
        OrdinaryLoadBatch batch,
        IReadOnlyList<StagedGraphPlan> plans,
        IReadOnlySet<string> targetKeys,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken)
    {
        // Exclude retiring entries and projected replacements before constructing any
        // non-collectible context. A later fallback can change the retained projection,
        // so the final materialized catalog is checked again before persistence.
        var selected = plans.Where(plan => plan.Projections.Keys.All(targetKeys.Contains)).ToArray();
        var replaced = selected.SelectMany(static plan => plan.Projections.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retained = targetKeys.Where(key => !replaced.Contains(key)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = selected.SelectMany(static plan => plan.Candidates).ToArray();
        var conflicts = _hostIntegratedResolutionCatalog.FindProspectiveCandidateConflicts(retained, candidates);

        foreach (var plan in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var error = GetGraphConflict(plan.Projections.Keys, conflicts);
            if (error is not null)
            {
                batch.RecordFailure(plan.Packages, error);
                continue;
            }

            StagePlannedGraph(batch, plan, sharedPolicy);
        }
    }

    private void StagePlannedGraph(
        OrdinaryLoadBatch batch,
        StagedGraphPlan plan,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy)
    {
        var (packages, projections, graphKey, mode, usesPerPackageContext, resolved, decision, _, leaseOwner) = plan;
        var inert = resolved.InertPackages.Select(package => projections[BuildKey(package.Id, package.Version)]).ToArray();
        if (resolved.LoadablePackages.Count == 0)
        {
            batch.Graphs.Add(new(graphKey, projections.Values.ToArray(), inert, null, [], null, []));
            return;
        }

        var lease = leaseOwner?.Lease;
        using var readCheckpoint = lease is null ? null : PackageAssemblyReadCheckpoint.EnterGraph(lease.Snapshot);
        try
        {
            var contextKey = usesPerPackageContext ? BuildKey(packages[0].Id, packages[0].Version) : graphKey;
            var context = CreateGraphLoadContext(contextKey, mode, resolved.LoadablePackages,
                packages.Select(static package => package.InstallPath).ToArray(), sharedPolicy, leaseOwner);
            batch.StagedContexts.Add(context);
            LoadGraphAssemblies(context, resolved.LoadablePackages);
            var entries = mode == PackageLoadMode.HostIntegrated
                ? HostIntegratedAssemblyResolutionCatalog.CreateGraphEntries(graphKey, decision.Selections,
                    MaterializeHostIntegratedAssemblies(context, resolved.LoadablePackages, sharedPolicy))
                : [];

            var sessions = resolved.LoadablePackages.Select(package => new PackageLoadSession(
                package.Id, package.Version, package.InstallPath, contextKey, DateTimeOffset.UtcNow,
                IsLoaded: true, LastError: null, mode, FrameworkIntegrationSafe: mode == PackageLoadMode.HostIntegrated,
                LoadModeDiagnostics: ResolveLoadModeDiagnostics(decision.DiagnosticsByPackageKey,
                    BuildKey(package.Id, package.Version)))).ToArray();
            var cacheEntry = usesPerPackageContext ? null : new LoadedGraphCacheEntry(mode,
                sessions.Select(static session => BuildKey(session.PackageId, session.Version))
                    .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).ToArray(),
                OrderProjections(projections.Values));

            batch.Graphs.Add(new(graphKey, projections.Values.ToArray(), inert, context, sessions, cacheEntry, entries));
            batch.Loaded.AddRange(sessions);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            batch.RecordFailure(packages, exception.Message);
        }
    }

    private static string? GetGraphConflict(IEnumerable<string> packageKeys, IReadOnlyDictionary<string, string> conflicts)
    {
        var errors = packageKeys.Where(conflicts.ContainsKey).Select(key => conflicts[key]).Distinct(StringComparer.Ordinal).ToArray();
        return errors.Length == 0 ? null : string.Join(" ", errors);
    }

    private sealed record StagedGraphPlan(
        IReadOnlyList<ResolvedPackage> Packages,
        IReadOnlyDictionary<string, PackageProjection> Projections,
        string GraphKey,
        PackageLoadMode Mode,
        bool UsesPerPackageContext,
        GraphPackageResolution Assets,
        PackageGraphLoadModeDecision Decision,
        IReadOnlyList<HostIntegratedAssemblyResolutionCandidate> Candidates,
        PackageGraphUseLeaseOwner? LeaseOwner);

    internal sealed class OrdinaryLoadBatch : IDisposable
    {
        private readonly PackageLoader _loader;
        private readonly long _originalVersion;
        private readonly List<AssemblyLoadContext> _retiredContexts = [];
        private readonly HashSet<AssemblyLoadContext> _publishedContexts = [];
        private CommitProjection? _projection;
        private bool _committed;
        private bool _disposed;

        internal OrdinaryLoadBatch(PackageLoader loader, long originalVersion)
        {
            _loader = loader;
            _originalVersion = originalVersion;
        }

        internal List<PackageLoadSession> Loaded { get; } = [];
        private Dictionary<string, string> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal List<StagedGraph> Graphs { get; } = [];
        internal List<AssemblyLoadContext> StagedContexts { get; } = [];

        internal PackageLoadResult Result => new(Loaded, Failures);

        internal void RecordFailure(IReadOnlyList<ResolvedPackage> packages, string message)
        {
            foreach (var package in packages)
                Failures[package.Id] = message;
        }

        internal IReadOnlyDictionary<string, string> ValidateForCommit(
            IReadOnlyList<ActivePackage> activePackages,
            IReadOnlySet<string> rejectedPackageIds)
        {
            ArgumentNullException.ThrowIfNull(activePackages);
            ArgumentNullException.ThrowIfNull(rejectedPackageIds);
            ObjectDisposedException.ThrowIf(_disposed, this);

            var active = activePackages.ToDictionary(
                static package => BuildKey(package.PackageId, package.Version), StringComparer.OrdinalIgnoreCase);
            var acceptedGraphs = Graphs.Where(graph => graph.Packages.All(projection =>
                    Matches(projection, active) && !rejectedPackageIds.Contains(active[projection.PackageKey].PackageId)))
                .ToArray();
            var replacedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var graph in acceptedGraphs)
            {
                foreach (var package in graph.Packages)
                {
                    if (!replacedKeys.Add(package.PackageKey))
                        throw new InvalidOperationException($"Package '{package.PackageKey}' is activated by more than one staged graph.");
                }
            }

            lock (_loader._loadContextSync)
            {
                if (_committed || _loader._activationProjectionVersion != _originalVersion ||
                    _loader._validatedActivationBatch is not null)
                {
                    throw new InvalidOperationException("The loaded package projection changed while activation was staged.");
                }

                var retainedKeys = _loader._loadedPackageProjections.Values.Where(ShouldRetain)
                    .Select(static projection => projection.PackageKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var invalidKeys = _loader._loadedPackageProjections.Values
                    .Concat(_loader._inertPackageProjections.Values)
                    .Where(projection => !ShouldRetain(projection))
                    .Select(static projection => projection.PackageKey)
                    .Concat(_loader._sessions.Keys.Where(key => !active.ContainsKey(key)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var catalog = _loader._hostIntegratedResolutionCatalog.PrepareActiveProjection(retainedKeys,
                    acceptedGraphs.SelectMany(static graph => graph.CatalogEntries).ToArray(), out var conflicts);
                var rejectedGraphs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (catalog is null)
                {
                    foreach (var graph in acceptedGraphs)
                    {
                        var packageKeys = graph.Packages.Select(static package => package.PackageKey);
                        var message = GetGraphConflict(packageKeys, conflicts);
                        if (message is null)
                            continue;

                        foreach (var package in graph.Packages)
                        {
                            var packageId = active[package.PackageKey].PackageId;
                            Failures[packageId] = message;
                            rejectedGraphs[packageId] = message;
                        }
                    }
                }
                else
                {
                    _projection = new(acceptedGraphs, invalidKeys, catalog);
                    _loader._validatedActivationBatch = this;
                }

                return rejectedGraphs;
            }

            bool ShouldRetain(PackageProjection projection) =>
                !replacedKeys.Contains(projection.PackageKey) && Matches(projection, active);
        }

        private static bool Matches(PackageProjection projection, IReadOnlyDictionary<string, ActivePackage> active) =>
            active.TryGetValue(projection.PackageKey, out var package) &&
            string.Equals(projection.InstallPath, package.InstallPath, StringComparison.Ordinal) &&
            (package.ArchiveHash is null ||
                string.Equals(projection.PackageContentHash, package.ArchiveHash, StringComparison.Ordinal));

        internal void Commit()
        {
            lock (_loader._loadContextSync)
            {
                if (!ReferenceEquals(_loader._validatedActivationBatch, this) || _projection is null)
                    throw new InvalidOperationException("Validate the staged package activation before committing it.");

                foreach (var key in _projection.InvalidKeys)
                {
                    if (_loader._contexts.TryRemove(key, out var context))
                        _retiredContexts.Add(context);
                    _loader._sessions.TryRemove(key, out _);
                    _loader._loadedPackageProjections.TryRemove(key, out _);
                    _loader.ClearInert(key);
                    _loader.RemoveKeyFromLoadedGraphs(key);
                }

                foreach (var graph in _projection.Graphs)
                {
                    foreach (var inert in graph.Inert)
                        _loader.MarkInert(inert);

                    if (graph.Context is null)
                        continue;

                    _publishedContexts.Add(graph.Context);
                    foreach (var session in graph.Sessions)
                    {
                        var key = BuildKey(session.PackageId, session.Version);
                        _loader._contexts[key] = graph.Context;
                        _loader._loadedPackageProjections[key] = graph.Packages.Single(projection =>
                            string.Equals(projection.PackageKey, key, StringComparison.OrdinalIgnoreCase));
                        _loader._sessions[key] = session;
                    }

                    if (graph.CacheEntry is not null)
                        _loader._loadedGraphs[graph.GraphKey] = graph.CacheEntry;
                }

                _loader._hostIntegratedResolutionCatalog.CommitActiveProjection(_projection.Catalog);
                _committed = true;
                _loader._activationProjectionVersion++;
                _loader._validatedActivationBatch = null;
            }
        }

        public void Dispose()
        {
            AssemblyLoadContext[] unload;
            lock (_loader._loadContextSync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                if (ReferenceEquals(_loader._validatedActivationBatch, this))
                    _loader._validatedActivationBatch = null;

                unload = _retiredContexts.Concat(StagedContexts.Where(context => !_publishedContexts.Contains(context)))
                    .Distinct().Where(context => context.IsCollectible && !_loader._contexts.Values.Contains(context))
                    .ToArray();
                _projection = null;
                Graphs.Clear();
                StagedContexts.Clear();
                _retiredContexts.Clear();
                _publishedContexts.Clear();
            }

            // Collectible context unload can invoke application handlers. The store and loader
            // authority locks must already be released before cleanup runs.
            foreach (var context in unload)
            {
                try
                {
                    context.Unload();
                }
                catch (Exception exception)
                {
                    StagedContextUnloadFailed(_loader._logger, context.Name, exception);
                }
            }
        }

        private sealed record CommitProjection(
            IReadOnlyList<StagedGraph> Graphs,
            IReadOnlyList<string> InvalidKeys,
            HostIntegratedAssemblyResolutionCatalog.ActiveProjection Catalog);
    }

    internal sealed record StagedGraph(
        string GraphKey,
        IReadOnlyList<PackageProjection> Packages,
        IReadOnlyList<PackageProjection> Inert,
        AssemblyLoadContext? Context,
        IReadOnlyList<PackageLoadSession> Sessions,
        LoadedGraphCacheEntry? CacheEntry,
        IReadOnlyList<HostIntegratedAssemblyResolutionEntry> CatalogEntries);

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Unable to unload package activation context {ContextName}.")]
    private static partial void StagedContextUnloadFailed(ILogger logger, string? contextName, Exception exception);
}
