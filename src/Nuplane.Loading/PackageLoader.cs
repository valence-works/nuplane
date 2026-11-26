
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Reconciliation;
using Nuplane.Store.Coordination;
using Nuplane.Registration;
using static Nuplane.Loading.PackageProjection;

namespace Nuplane.Loading;

/// <summary>
/// Loads package assemblies into isolated collectible load contexts, tracking sessions
/// and providing context removal for unloading. Resolves compatible runtime assets within
/// each package's install directory without requiring a package entry assembly.
/// </summary>
internal sealed partial class PackageLoader : IPackageLoader, IScopedPackageLoader
{
    private readonly PackageGraphMaterializer _materializer;
    private readonly PackageGraphLifetime _lifetime;
    private readonly PackageLoadModeSelector _loadModeSelector;
    private readonly IReadOnlyList<IPackageActivationGate> _activationGates;
    private readonly LoadingOptions _options;
    private readonly ILogger<PackageLoader> _logger;
    private readonly HostIntegratedAssemblyResolver? _hostIntegratedAssemblyResolver;
    private readonly PackageAssemblyAssetSelector _assetSelector;
    /// <summary>
    /// Initializes a new instance of <see cref="PackageLoader"/> with an optional shared assembly policy matcher
    /// and an optional set of activation gates consulted before any package graph is loaded.
    /// The trailing <c>hostTargetFrameworkOverride</c> replaces the current process's target framework for
    /// asset selection; it is <see langword="null"/> for a host, which always resolves assets for the
    /// framework it is itself running on.
    /// </summary>
    public PackageLoader(
        SharedAssemblyPolicyMatcher? matcher = null,
        HostIntegratedAssemblyResolutionCatalog? hostIntegratedResolutionCatalog = null,
        PackageLoadModeSelector? loadModeSelector = null,
        IEnumerable<IPackageLoadModeAdvisor>? loadModeAdvisors = null,
        IOptions<LoadingOptions>? options = null,
        ILogger<PackageLoader>? logger = null,
        HostIntegratedAssemblyResolver? hostIntegratedAssemblyResolver = null,
        IEnumerable<IPackageActivationGate>? activationGates = null,
        string? hostTargetFrameworkOverride = null,
        PackageStoreCompositionSnapshot? composition = null)
    {
        _loadModeSelector = loadModeSelector ?? new PackageLoadModeSelector(loadModeAdvisors, composition: composition);
        _activationGates = activationGates?.ToArray() ?? [];
        _options = options?.Value ?? new LoadingOptions();
        _logger = logger ?? NullLogger<PackageLoader>.Instance;
        _hostIntegratedAssemblyResolver = hostIntegratedAssemblyResolver;
        _assetSelector = new(hostTargetFrameworkOverride);
        _materializer = new(_assetSelector, matcher ?? new SharedAssemblyPolicyMatcher(), _logger);
        _lifetime = new(_assetSelector, _materializer,
            hostIntegratedResolutionCatalog ?? new HostIntegratedAssemblyResolutionCatalog(), _logger,
            requiresGraphUseLease: composition?.Mode == PackageStoreCompositionMode.CoordinatedParticipant);
    }

    /// <summary>
    /// Gets the active load sessions keyed by package-version key.
    /// </summary>
    public IReadOnlyDictionary<string, PackageLoadSession> Sessions => _lifetime.Sessions;

    /// <inheritdoc />
    public bool IsInertPackage(string packageId, string version) => _lifetime.IsInertPackage(packageId, version);

    bool IScopedPackageLoader.IsInertPackage(ResolvedPackage package, PackageGraphUseLease lease)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(lease);
        return _lifetime.IsInertPackage(CreatePackageProjection(package, lease));
    }

    /// <summary>
    /// Builds deterministic assembly scan candidates for the specified active package install path.
    /// </summary>
    public IReadOnlyList<AssemblyScanCandidate> BuildScanCandidates(string packageId, string installPath)
        => BuildScanCandidates(packageId, installPath, lease: null);

    internal IReadOnlyList<AssemblyScanCandidate> BuildScanCandidates(
        string packageId, string installPath, PackageGraphUseLease? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        if (lease is null)
            _lifetime.RefuseUnscopedRead();
        using var read = lease?.AcquireRead(installPath);
        return _assetSelector.BuildScanCandidates(packageId, installPath);
    }

    internal IReadOnlyList<AssemblyScanCandidate> BuildScanCandidates(
        ActivePackage package,
        PackageLoadSession session)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(session);
        if (!_lifetime.RequiresGraphUseLease)
            return BuildScanCandidates(package.PackageId, package.InstallPath);

        // Keep the exact session-to-context join stable until BuildScanCandidates has acquired the
        // counted lease read pin and finished enumeration. Context replacement uses this same lock.
        lock (_lifetime.SyncRoot)
        {
            var lease = _lifetime.GetGraphUseLeaseUnderLock(package, session);
            return BuildScanCandidates(package.PackageId, package.InstallPath, lease);
        }
    }

    /// <inheritdoc />
    public Task<PackageLoadResult> EnsureLoadedAsync(
        IReadOnlyList<ResolvedPackage> packages,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(sharedPolicy);
        _lifetime.RefuseUnscopedRead();

        var loaded = new List<PackageLoadSession>();
        var failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var projections = CreatePackageProjections(packages);
        lock (_lifetime.SyncRoot)
        {
            _lifetime.BeginLiveActivationMutation();
            _lifetime.EnsurePackagesLoaded(packages, sharedPolicy, cancellationToken, loaded, failed,
                packageProjections: projections);
        }

        return Task.FromResult<PackageLoadResult>(new(loaded, failed));
    }

    /// <inheritdoc />
    public async Task<PackageLoadResult> EnsureGraphLoadedAsync(
        IReadOnlyList<IReadOnlyList<ResolvedPackage>> packageGraphs,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageGraphs);
        ArgumentNullException.ThrowIfNull(sharedPolicy);
        _lifetime.RefuseUnscopedRead();

        var loaded = new List<PackageLoadSession>();
        var failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var packageGraph in packageGraphs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessGraphAsync(packageGraph, CreatePackageProjections(packageGraph),
                sharedPolicy, cancellationToken, graphLeaseOwner: null, loaded, failed).ConfigureAwait(false);
        }

        return new(loaded, failed);
    }

    Task<PackageLoadResult> IScopedPackageLoader.EnsureGraphLoadedAsync(
        IReadOnlyList<ScopedResolvedPackageGraph> packageGraphs,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken)
        => EnsureGraphLoadedWithLeasesAsync(packageGraphs, sharedPolicy, cancellationToken);

    private async Task<PackageLoadResult> EnsureGraphLoadedWithLeasesAsync(
        IReadOnlyList<ScopedResolvedPackageGraph> packageGraphs,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageGraphs);
        ArgumentNullException.ThrowIfNull(sharedPolicy);

        var work = packageGraphs.Select(packageGraph => packageGraph is null
            ? null
            : new LeasedGraphWorkItem(packageGraph.Packages, packageGraph.LeaseOwner,
                () => ScopedResolvedPackageGraphValidator.Validate(packageGraph))).ToArray();
        return await EnsureLeasedGraphsLoadedAsync(work, sharedPolicy, cancellationToken).ConfigureAwait(false);
    }

    async Task<PackageLoadResult> IScopedPackageLoader.EnsureProtectedRecoveryGraphsLoadedAsync(
        IReadOnlyList<LeasedProtectedRecoveryGraphSelection> graphSelections,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graphSelections);
        ArgumentNullException.ThrowIfNull(sharedPolicy);

        var work = graphSelections.Select(selection => selection is null
            ? null
            : new LeasedGraphWorkItem(selection.Packages, selection.LeaseOwner,
                () => ScopedProtectedRecoveryGraphValidator.Validate(selection))).ToArray();
        return await EnsureLeasedGraphsLoadedAsync(work, sharedPolicy, cancellationToken).ConfigureAwait(false);
    }

    async Task<PackageLoadResult> IScopedPackageLoader.EnsureProtectedActiveGraphsLoadedAsync(
        IReadOnlyList<LeasedProtectedActiveGraphSelection> graphSelections,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graphSelections);
        ArgumentNullException.ThrowIfNull(sharedPolicy);

        var work = graphSelections.Select(selection => selection is null
            ? null
            : new LeasedGraphWorkItem(selection.Packages, selection.LeaseOwner,
                () => ScopedProtectedActiveGraphValidator.Validate(selection))).ToArray();
        return await EnsureLeasedGraphsLoadedAsync(work, sharedPolicy, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PackageLoadResult> EnsureLeasedGraphsLoadedAsync(
        IReadOnlyList<LeasedGraphWorkItem?> graphWork,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken)
    {

        // Freeze caller-owned collections before any callback can await or mutate them.
        var graphs = graphWork.ToArray();
        var copiedSharedPolicy = sharedPolicy.ToArray();
        var owners = graphs.Where(static graph => graph is not null)
            .Select(static graph => graph!.LeaseOwner).Distinct().ToArray();

        var readPins = new List<IDisposable>();
        var loaded = new List<PackageLoadSession>();
        var failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Exception? operationFailure = null;
        try
        {
            if (owners.Length != graphs.Length)
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                    "Each selected graph must have a distinct published lease owner.");

            PrepareLeasedGraphReads(graphs, readPins);

            foreach (var graph in graphs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var selected = graph!;
                var packages = selected.Packages;
                var owner = selected.LeaseOwner;
                var graphLease = owner.Lease;
                var graphKey = BuildGraphKey(packages);
                var packageProjections = selected.PackageProjections!;
                var graphLeases = new[] { graphLease };
                using var readCheckpointScope = PackageAssemblyReadCheckpoint.EnterGraph(graphLease.Snapshot);
                PackageAssemblyReadCheckpoint.BeforeLoadModeSelection();
                var graphDecision = await _loadModeSelector.SelectGraphAsync(
                    packages, _options, graphKey, cancellationToken, graphLeases).ConfigureAwait(false);
                var selections = graphDecision.Selections;
                var usesPerPackageContexts = UsesPerPackageContexts(packages, selections);
                var graphLoadMode = PackageGraphLifetime.ResolveGraphLoadMode(selections);

                // Consume an exact cache hit under the same monitor as its identity decision. Cache misses
                // leave the monitor before any activation gate is consulted.
                if (_lifetime.TryReuseLoadedGraph(packages, selections, copiedSharedPolicy, cancellationToken,
                        loaded, failed, packageProjections, graphDecision.DiagnosticsByPackageKey, owner,
                        graphKey, graphLoadMode, usesPerPackageContexts))
                {
                    continue;
                }

                var activationBlockMessage = await EvaluateActivationGatesAsync(
                    packages,
                    graphKey,
                    graphLoadMode,
                    cancellationToken,
                    graphLeases).ConfigureAwait(false);

                if (activationBlockMessage is not null)
                {
                    lock (_lifetime.SyncRoot)
                    {
                        _lifetime.BeginLiveActivationMutation();
                        _lifetime.RecordGraphFailure(
                            packages,
                            graphKey,
                            graphLoadMode,
                            usesPerPackageContexts,
                            activationBlockMessage,
                            failed,
                            graphDecision.DiagnosticsByPackageKey);
                    }
                    continue;
                }

                lock (_lifetime.SyncRoot)
                {
                    _lifetime.BeginLiveActivationMutation();
                    if (usesPerPackageContexts)
                    {
                        _lifetime.EnsurePackagesLoaded(packages, copiedSharedPolicy, cancellationToken, loaded, failed,
                            packageProjections, graphDecision.DiagnosticsByPackageKey, owner);
                    }
                    else
                    {
                        _lifetime.EnsureGraphLoaded(packages, selections, copiedSharedPolicy, loaded, failed,
                            packageProjections, graphDecision.DiagnosticsByPackageKey, owner);
                    }
                }
            }

            return new(loaded, failed);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            throw;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            for (var index = readPins.Count - 1; index >= 0; index--)
            {
                try { readPins[index].Dispose(); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }

            // Transferred owners ignore caller disposal; blocked, inert, canceled, and failed-before-context
            // owners release only after all counted pre-context reads have drained.
            foreach (var owner in owners)
            {
                try { await owner.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }

            if (cleanupFailures.Count != 0)
            {
                if (operationFailure is not null)
                    cleanupFailures.Insert(0, operationFailure);
                throw new AggregateException("Scoped graph loading failed and one or more graph-use owners did not drain cleanly.",
                    cleanupFailures);
            }
        }
    }

    private void PrepareLeasedGraphReads(IReadOnlyList<LeasedGraphWorkItem?> graphs, List<IDisposable> readPins)
    {
        _loadModeSelector.ValidateScopedParticipants();
        if (_activationGates.Any(static gate =>
                gate is not IScopedPackageActivationGate and not IPackagePathIndependentActivationGate))
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "Enrolled package loading requires explicitly scoped or path-independent activation gates.");
        }

        // Validate every immutable envelope before the first package read or user callback.
        foreach (var graph in graphs)
        {
            var selected = graph ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                "A leased graph collection contains a null entry.");
            selected.Validate();
            selected.PackageProjections = CreatePackageProjections(selected.Packages, selected.LeaseOwner.Lease);
        }

        // Hold counted exact-path pins across metadata, gates, package enumeration, and transfer
        // into the actual load context. No root operation borrow is retained or reacquired here.
        foreach (var graph in graphs)
        {
            var selected = graph!;
            var lease = selected.LeaseOwner.Lease;
            foreach (var package in selected.Packages)
                readPins.Add(lease.AcquireRead(package.InstallPath));
        }
    }

    private sealed class LeasedGraphWorkItem(
        IReadOnlyList<ResolvedPackage> packages,
        PackageGraphUseLeaseOwner leaseOwner,
        Action validate)
    {
        internal IReadOnlyList<ResolvedPackage> Packages { get; } = packages;
        internal PackageGraphUseLeaseOwner LeaseOwner { get; } = leaseOwner;
        internal Action Validate { get; } = validate;
        internal IReadOnlyDictionary<string, PackageProjection>? PackageProjections { get; set; }
    }

    private async Task ProcessGraphAsync(
        IReadOnlyList<ResolvedPackage> packageGraph,
        IReadOnlyDictionary<string, PackageProjection> packageProjections,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken,
        PackageGraphUseLeaseOwner? graphLeaseOwner,
        List<PackageLoadSession> loaded,
        Dictionary<string, string> failed)
    {
        var graphKey = BuildGraphKey(packageGraph);
        var graphDecision = await _loadModeSelector.SelectGraphAsync(
            packageGraph, _options, graphKey, cancellationToken).ConfigureAwait(false);
        var selections = graphDecision.Selections;
        var usesPerPackageContexts = UsesPerPackageContexts(packageGraph, selections);
        var graphLoadMode = PackageGraphLifetime.ResolveGraphLoadMode(selections);

        if (_lifetime.TryReuseLoadedGraph(packageGraph, selections, sharedPolicy, cancellationToken,
                loaded, failed, packageProjections, graphDecision.DiagnosticsByPackageKey, graphLeaseOwner,
                graphKey, graphLoadMode, usesPerPackageContexts))
        {
            return;
        }

        var activationBlockMessage = await EvaluateActivationGatesAsync(
            packageGraph,
            graphKey,
            graphLoadMode,
            cancellationToken).ConfigureAwait(false);

        lock (_lifetime.SyncRoot)
        {
            _lifetime.BeginLiveActivationMutation();
            if (activationBlockMessage is not null)
            {
                _lifetime.RecordGraphFailure(
                    packageGraph,
                    graphKey,
                    graphLoadMode,
                    usesPerPackageContexts,
                    activationBlockMessage,
                    failed,
                    graphDecision.DiagnosticsByPackageKey);
            }
            else if (usesPerPackageContexts)
            {
                _lifetime.EnsurePackagesLoaded(packageGraph, sharedPolicy, cancellationToken, loaded, failed,
                    packageProjections, graphDecision.DiagnosticsByPackageKey, graphLeaseOwner);
            }
            else
            {
                _lifetime.EnsureGraphLoaded(packageGraph, selections, sharedPolicy, loaded, failed,
                    packageProjections, graphDecision.DiagnosticsByPackageKey, graphLeaseOwner);
            }
        }
    }

    private static IReadOnlyDictionary<string, PackageProjection> CreatePackageProjections(
        IReadOnlyList<ResolvedPackage> packages,
        PackageGraphUseLease? lease = null)
    {
        var projections = new Dictionary<string, PackageProjection>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            ArgumentNullException.ThrowIfNull(package);
            var key = BuildKey(package.Id, package.Version);
            var projection = CreatePackageProjection(package, lease);
            if (!projections.TryAdd(key, projection))
            {
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                    $"A package graph contains more than one exact install projection for '{key}'.",
                    lease?.Snapshot.Roots.Count == 1 ? lease.Snapshot.Roots[0] : null);
            }
        }

        return projections;
    }

    private static PackageProjection CreatePackageProjection(
        ResolvedPackage package,
        PackageGraphUseLease? lease)
    {
        var key = BuildKey(package.Id, package.Version);
        return new PackageProjection(
            key,
            package.InstallPath,
            package.PackageContentHash,
            lease?.GetInstallIdentityForExactPath(package.InstallPath));
    }

    private static IReadOnlyDictionary<string, PackageProjection> CreatePackageProjections(
        ScopedResolvedPackageGraph packageGraph) =>
        CreatePackageProjections(packageGraph.Packages, packageGraph.LeaseOwner.Lease);

    // The single definition of the load-path dispatch rule: a single-package, all-collectible graph is loaded
    // through per-package contexts instead of one shared graph context. Both the dispatch itself and the
    // activation-gate pre-check ask this, so they always agree on which path a graph would take.
    private static bool UsesPerPackageContexts(
        IReadOnlyList<ResolvedPackage> packageGraph,
        IReadOnlyList<PackageLoadModeSelection> selections) =>
        packageGraph.Count <= 1 && selections.All(static selection => selection.LoadMode == PackageLoadMode.Collectible);

    /// <summary>
    /// Consults every registered activation gate for a graph that is about to be loaded, in registration
    /// order and sequentially. Returns the failure message that must be surfaced for the graph, or
    /// <see langword="null"/> when no gate refused it and loading may proceed.
    /// </summary>
    private async ValueTask<string?> EvaluateActivationGatesAsync(
        IReadOnlyList<ResolvedPackage> packageGraph,
        string graphKey,
        PackageLoadMode graphLoadMode,
        CancellationToken cancellationToken,
        IReadOnlyList<PackageGraphUseLease>? graphUseLeases = null)
    {
        if (_activationGates.Count == 0 || packageGraph.Count == 0)
            return null;

        var context = new PackageActivationContext(
            graphKey,
            graphLoadMode,
            packageGraph
                .OrderBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static package => package.Version, StringComparer.OrdinalIgnoreCase)
                .ToArray())
        {
            GraphUseLeases = graphUseLeases ?? Array.Empty<PackageGraphUseLease>()
        };
        var packageKeys = string.Join(", ", context.Packages.Select(static package => BuildKey(package.Id, package.Version)));

        // Every gate is consulted even after one blocks, so an operator sees every blocker at once.
        var blockReasons = new List<string>();
        foreach (var gate in _activationGates)
        {
            var gateType = gate.GetType().FullName ?? gate.GetType().Name;
            PackageActivationGateResult? result;

            try
            {
                result = await gate.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller asked to stop. That is not a gate decision and must stay a cancellation.
                throw;
            }
            catch (Exception ex)
            {
                // Fail closed: a gate that cannot answer never means "allow".
                _logger.PackageActivationGateFailed(gateType, graphKey, packageKeys, ex);
                blockReasons.Add($"gate '{gateType}' failed to evaluate activation and was treated as a block: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (result is null)
            {
                const string missingResultReason = "the gate returned no activation result";
                _logger.PackageActivationGateBlocked(gateType, graphKey, packageKeys, missingResultReason);
                blockReasons.Add($"gate '{gateType}' was treated as a block because {missingResultReason}");
                continue;
            }

            if (result.IsAllowed)
            {
                continue;
            }

            var reason = result.Reason ?? "no reason was supplied";
            _logger.PackageActivationGateBlocked(gateType, graphKey, packageKeys, reason);
            blockReasons.Add($"gate '{gateType}' blocked activation: {reason}");
        }

        return blockReasons.Count == 0
            ? null
            : $"Activation of package graph '{graphKey}' ({packageKeys}) was blocked before loading. {string.Join(" ", blockReasons)}";
    }

    /// <inheritdoc />
    public IReadOnlyList<string> UnloadContextsNotActive(IReadOnlyDictionary<string, string> activeVersionById) =>
        _lifetime.UnloadContextsNotActive(activeVersionById);

    /// <inheritdoc />
    public bool TryRemoveContext(string packageId, string version, out PackageLoadContextHandle? context) =>
        _lifetime.TryRemoveContext(packageId, version, out context);

    /// <inheritdoc />
    public bool TryGetContext(string packageId, string version, out PackageLoadContextHandle? context) =>
        _lifetime.TryGetContext(packageId, version, out context);

    internal PackageGraphUseLease? GetGraphUseLease(AssemblyLoadContext context) => _lifetime.GetGraphUseLease(context);

    internal static string BuildGraphKey(IReadOnlyList<ResolvedPackage> packages) => PackageProjection.BuildGraphKey(packages);

    internal static bool IsSupportedTargetFrameworkMoniker(string targetFrameworkMoniker) =>
        PackageAssemblyAssetSelector.IsSupportedTargetFrameworkMoniker(targetFrameworkMoniker);

}
