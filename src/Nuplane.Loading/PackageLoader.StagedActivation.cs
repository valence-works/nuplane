using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Reconciliation;
using static Nuplane.Loading.PackageProjection;

namespace Nuplane.Loading;

internal sealed partial class PackageLoader
{
    internal async Task<PackageGraphLifetime.StagedLoadBatch> StageOrdinaryGraphsAsync(
        IReadOnlyList<IReadOnlyList<ResolvedPackage>> graphs,
        IReadOnlyDictionary<string, string> prospectiveActiveVersions,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graphs);
        ArgumentNullException.ThrowIfNull(prospectiveActiveVersions);
        ArgumentNullException.ThrowIfNull(sharedPolicy);
        _lifetime.RefuseUnscopedRead();

        var packages = graphs.Select(static graph => graph.ToArray()).ToArray();
        var policy = sharedPolicy.ToArray();
        var targetKeys = GetActiveKeys(prospectiveActiveVersions);
        var batch = _lifetime.BeginActivationBatch();
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

    internal async Task<PackageGraphLifetime.StagedLoadBatch> StageLeasedGraphsAsync(
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
        var batch = _lifetime.BeginActivationBatch();
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

    private static HashSet<string> GetActiveKeys(IReadOnlyDictionary<string, string> versions) =>
        versions.Select(static version => BuildKey(version.Key, version.Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async Task<StagedGraphPlan?> PlanGraphAsync(
        PackageGraphLifetime.StagedLoadBatch batch,
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
        var mode = PackageGraphLifetime.ResolveGraphLoadMode(decision.Selections);
        var usesPerPackageContext = UsesPerPackageContexts(packages, decision.Selections);
        lock (_lifetime.SyncRoot)
        {
            if (_lifetime.TryGetExactGraphSessions(packages, graphKey, mode, usesPerPackageContext, projections, out var sessions))
            {
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
            var resolved = _materializer.ResolvePackages(packages);
            resolved.ThrowIfUnusable(graphKey);

            var candidates = mode == PackageLoadMode.HostIntegrated
                ? _materializer.BuildResolutionCandidates(graphKey, resolved.LoadablePackages, sharedPolicy)
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
        PackageGraphLifetime.StagedLoadBatch batch,
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
        var conflicts = _lifetime.FindProspectiveCandidateConflicts(retained, candidates);

        foreach (var plan in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var error = PackageGraphLifetime.GetGraphConflict(plan.Projections.Keys, conflicts);
            if (error is not null)
            {
                batch.RecordFailure(plan.Packages, error);
                continue;
            }

            StagePlannedGraph(batch, plan, sharedPolicy);
        }
    }

    private void StagePlannedGraph(
        PackageGraphLifetime.StagedLoadBatch batch,
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
            var context = _materializer.CreateContext(contextKey, mode, resolved.LoadablePackages,
                packages.Select(static package => package.InstallPath).ToArray(), sharedPolicy, leaseOwner);
            batch.StagedContexts.Add(context);
            PackageGraphMaterializer.LoadAssemblies(context, resolved.LoadablePackages);
            var entries = mode == PackageLoadMode.HostIntegrated
                ? HostIntegratedAssemblyResolutionCatalog.CreateGraphEntries(graphKey, decision.Selections,
                    _materializer.GetHostIntegratedAssemblies(context, resolved.LoadablePackages, sharedPolicy))
                : [];

            var sessions = resolved.LoadablePackages.Select(package => new PackageLoadSession(
                package.Id, package.Version, package.InstallPath, contextKey, DateTimeOffset.UtcNow,
                IsLoaded: true, LastError: null, mode, FrameworkIntegrationSafe: mode == PackageLoadMode.HostIntegrated,
                LoadModeDiagnostics: PackageGraphLifetime.ResolveLoadModeDiagnostics(decision.DiagnosticsByPackageKey,
                    BuildKey(package.Id, package.Version)))).ToArray();
            var cacheEntry = usesPerPackageContext ? null : new LoadedGraphCacheEntry(mode,
                sessions.Select(static session => BuildKey(session.PackageId, session.Version))
                    .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).ToArray(),
                PackageGraphLifetime.OrderProjections(projections.Values));

            batch.Graphs.Add(new(graphKey, projections.Values.ToArray(), inert, context, sessions, cacheEntry, entries));
            batch.Loaded.AddRange(sessions);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            batch.RecordFailure(packages, exception.Message);
        }
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
}
