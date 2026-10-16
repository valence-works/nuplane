using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Events;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;

namespace Nuplane.Hosting;

/// <summary>
/// Recovers a degraded startup cycle by re-activating the last-known-good package set recorded in
/// the store.
/// </summary>
/// <remarks>
/// <para>
/// Recovery reads the store and, on success, republishes the last-known-good packages as reconciled
/// — the same read-then-act shape as a reconciliation cycle. It therefore takes the same
/// cross-process <see cref="IStoreLock"/> a reconciliation cycle takes, for the same reason: without
/// it, a startup recovery could act on a state file a concurrent <c>NuplaneRestore</c> run or a
/// second host process is mid-write on.
/// </para>
/// <para>
/// Unlike a reconciliation cycle, which skips instantly and simply tries again on its next poll,
/// startup recovery has no next poll: it is the host's last attempt to establish a package set.
/// Losing the store lock to a short-lived, ordinary race — a concurrent <c>NuplaneRestore</c> run, a
/// second host process starting at the same time — should not fail startup by itself. So recovery
/// waits for the lock, polling up to
/// <see cref="ReconciliationOptions.StartupRecoveryStoreLockTimeout"/>, honouring cancellation
/// throughout. Only once that timeout elapses does recovery give up: it does nothing — no read, no
/// republish — and reports <see cref="LastKnownGoodStartupRecoveryResult.StoreLockUnavailableReason"/>,
/// which a host configured with <c>StartupFailurePolicy.UseLastKnownGood</c> treats as any other
/// failed recovery — startup fails, because the host genuinely could not establish its package set.
/// </para>
/// </remarks>
internal sealed class LastKnownGoodStartupRecoveryService : ILastKnownGoodStartupRecoveryService
{
    private static readonly TimeSpan StoreLockPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IStoreRegistry _storeRegistry;
    private readonly IObserverEventDispatcher _observerEventDispatcher;
    private readonly StartupRecoveryState _startupRecoveryState;
    private readonly IReadOnlyList<ICycleFailureContributor> _cycleFailureContributors;
    private readonly IStoreLock? _storeLock;
    private readonly IPackageStoreAdmission? _installPathAdmission;
    private readonly IPackageStoreAdmission? _configuredRootAdmission;
    private readonly bool _coordinatedParticipantMode;
    private readonly ICoordinatedStoreRegistry? _coordinatedStoreRegistry;
    private readonly PackageGraphUseLeaseAcquisition? _graphUseLeaseAcquisition;
    private readonly ITrustedPackageStoreRootCatalog? _trustedRootCatalog;
    private readonly IScopedObserverEventDispatcher? _scopedObserverEventDispatcher;
    private readonly ILeasedPackageGraphRecoveryLoadingObserver? _recoveryLoadingObserver;
    private readonly bool _leasedLoadingObserverConfigured;
    private readonly TimeSpan _storeLockTimeout;

    /// <summary>
    /// Initializes recovery with the runtime collaborators and optional cycle-failure contributor.
    /// </summary>
    /// <remarks>
    /// An instance built through this constructor never takes the cross-process store lock, even
    /// when <see cref="ReconciliationOptions.EnableStoreLock"/> is <see langword="true"/>: it is the
    /// low-level, test-composition path that passes collaborators directly, and it names no
    /// <see cref="IStoreLock"/> to take one with. <c>AddNuplane</c> is the path that supplies the
    /// lock, through dependency injection.
    /// </remarks>
    /// <param name="storeRegistry">The store registry.</param>
    /// <param name="observerEventDispatcher">The observer event dispatcher.</param>
    /// <param name="startupRecoveryState">Startup recovery state to update with the outcome.</param>
    /// <param name="cycleFailureContributors">Optional contributors of per-cycle failure information from external modules.</param>
    /// <param name="reconciliationOptions">
    /// Optional reconciliation options carrying <see cref="ReconciliationOptions.StartupRecoveryStoreLockTimeout"/>.
    /// Unused on this path, since there is no store lock to wait for; defaulted when omitted so
    /// low-level callers need not supply it.
    /// </param>
    public LastKnownGoodStartupRecoveryService(
        IStoreRegistry storeRegistry,
        IObserverEventDispatcher observerEventDispatcher,
        StartupRecoveryState startupRecoveryState,
        IEnumerable<ICycleFailureContributor>? cycleFailureContributors = null,
        IOptions<ReconciliationOptions>? reconciliationOptions = null)
        : this(
            storeRegistry,
            observerEventDispatcher,
            startupRecoveryState,
            cycleFailureContributors,
            reconciliationOptions,
            storeLock: null,
            packageStoreAdmission: null)
    {
    }

    /// <summary>
    /// Initializes recovery with the runtime collaborators, reconciliation options, and the
    /// cross-process store lock. Used by dependency injection, which always supplies
    /// <paramref name="storeLock"/>.
    /// </summary>
    /// <param name="storeRegistry">The store registry.</param>
    /// <param name="observerEventDispatcher">The observer event dispatcher.</param>
    /// <param name="startupRecoveryState">Startup recovery state to update with the outcome.</param>
    /// <param name="cycleFailureContributors">Optional contributors of per-cycle failure information from external modules.</param>
    /// <param name="reconciliationOptions">Reconciliation options carrying <see cref="ReconciliationOptions.StartupRecoveryStoreLockTimeout"/>.</param>
    /// <param name="storeLock">
    /// Cross-process lock on the store this service reads and may transitively write, or
    /// <see langword="null"/> on the low-level test-composition path, which passes collaborators
    /// directly and so names no resolved state file. Recovery then runs exactly as it did before the
    /// store lock existed.
    /// </param>
    /// <param name="packageStoreAdmission">
    /// Configured-root membership admission acquired before state reads by dependency injection.
    /// Explicitly supplied admissions also enable protected install-path classification in a
    /// low-level composition.
    /// </param>
    /// <param name="coordinatedParticipantMode">Whether this composition requires coordinated participant admission and must refuse the unscoped recovery path.</param>
    /// <param name="coordinatedStoreRegistry">The selected registry's coordinated state reader.</param>
    /// <param name="graphUseLeaseAcquisition">The built-in native graph-use publisher for recovered installs.</param>
    /// <param name="trustedRootCatalog">The immutable configured-root catalog used to derive exact install candidates.</param>
    /// <param name="scopedObserverEventDispatcher">The validated dispatcher that invokes observers under the live root owner.</param>
    /// <param name="recoveryLoadingObserver">The selected Loading bridge for persisted recovery graphs, when Loading is installed.</param>
    /// <param name="leasedLoadingObserverConfigured">Whether a leased loader is selected and therefore must support recovery handoff.</param>
    internal LastKnownGoodStartupRecoveryService(
        IStoreRegistry storeRegistry,
        IObserverEventDispatcher observerEventDispatcher,
        StartupRecoveryState startupRecoveryState,
        IEnumerable<ICycleFailureContributor>? cycleFailureContributors,
        IOptions<ReconciliationOptions>? reconciliationOptions,
        IStoreLock? storeLock,
        IPackageStoreAdmission? packageStoreAdmission = null,
        bool coordinatedParticipantMode = false,
        ICoordinatedStoreRegistry? coordinatedStoreRegistry = null,
        PackageGraphUseLeaseAcquisition? graphUseLeaseAcquisition = null,
        ITrustedPackageStoreRootCatalog? trustedRootCatalog = null,
        IScopedObserverEventDispatcher? scopedObserverEventDispatcher = null,
        ILeasedPackageGraphRecoveryLoadingObserver? recoveryLoadingObserver = null,
        bool leasedLoadingObserverConfigured = false)
    {
        _storeRegistry = storeRegistry ?? throw new ArgumentNullException(nameof(storeRegistry));
        _observerEventDispatcher = observerEventDispatcher ?? throw new ArgumentNullException(nameof(observerEventDispatcher));
        _startupRecoveryState = startupRecoveryState ?? throw new ArgumentNullException(nameof(startupRecoveryState));
        _cycleFailureContributors = cycleFailureContributors?.ToArray() ?? [];
        _storeLockTimeout = (reconciliationOptions?.Value ?? new ReconciliationOptions()).StartupRecoveryStoreLockTimeout;
        _storeLock = storeLock;
        _configuredRootAdmission = packageStoreAdmission;
        _installPathAdmission = packageStoreAdmission;
        _coordinatedParticipantMode = coordinatedParticipantMode;
        _coordinatedStoreRegistry = coordinatedStoreRegistry;
        _graphUseLeaseAcquisition = graphUseLeaseAcquisition;
        _trustedRootCatalog = trustedRootCatalog;
        _scopedObserverEventDispatcher = scopedObserverEventDispatcher;
        _recoveryLoadingObserver = recoveryLoadingObserver;
        _leasedLoadingObserverConfigured = leasedLoadingObserverConfigured;
    }

    public async Task<LastKnownGoodStartupRecoveryResult> TryRecoverAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        if (_coordinatedParticipantMode)
            return await TryRecoverCoordinatedParticipantAsync(correlationId, cancellationToken).ConfigureAwait(false);

        if (_configuredRootAdmission is null)
            return await TryRecoverAfterRootAdmissionAsync(correlationId, cancellationToken).ConfigureAwait(false);

        await using var rootAdmission = await _configuredRootAdmission.AcquireConfiguredRootOperationAsync(
            PackageStoreAdmissionKind.StartupRecovery, cancellationToken).ConfigureAwait(false);
        if (rootAdmission.Status == PackageStoreAdmissionStatus.Enrolled)
            throw RefuseEnrolled(rootAdmission.Root);

        return await TryRecoverAfterRootAdmissionAsync(correlationId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LastKnownGoodStartupRecoveryResult> TryRecoverCoordinatedParticipantAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        var admission = _configuredRootAdmission
            ?? throw RefuseEnrolled(root: null);
        var coordinatedRegistry = _coordinatedStoreRegistry
            ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "Participant startup recovery requires coordinated native state reads.");
        var graphAcquisition = _graphUseLeaseAcquisition
            ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "Participant startup recovery requires native graph-use publication.");
        var rootCatalog = _trustedRootCatalog
            ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "Participant startup recovery requires the frozen trusted-root catalog.");
        var scopedDispatcher = _scopedObserverEventDispatcher
            ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "Participant startup recovery requires a scope-aware observer dispatcher.");
        if (_leasedLoadingObserverConfigured && _recoveryLoadingObserver is null)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "The configured leased loader does not support persisted recovery-graph handoff.");

        scopedDispatcher.ValidateCoordinatedParticipants();

        var configuredDefaultRoots = rootCatalog.Roots.Where(static root =>
            string.Equals(root.Label, "default", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (configuredDefaultRoots.Length != 1 || !Path.IsPathFullyQualified(configuredDefaultRoots[0].RootPath))
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "Participant startup recovery requires exactly one fully qualified frozen default install root.");
        var configuredInstallRoot = configuredDefaultRoots[0].RootPath;

        var graphUseOwners = new List<PackageGraphUseLeaseOwner>();
        Exception? operationFailure = null;
        try
        {
            LeasedPackageGraphRecoveryLoadingHandoff? loadingHandoff = null;
            ResolvedPackage[] recoveredPackages = [];
            await using (var rootAdmission = await admission.AcquireConfiguredRootOperationAsync(
                PackageStoreAdmissionKind.StartupRecovery, cancellationToken).ConfigureAwait(false))
            {
                if (rootAdmission.Status != PackageStoreAdmissionStatus.Enrolled || rootAdmission.Owner is null ||
                    rootAdmission.Root is null || rootAdmission.Root != rootAdmission.Owner.Root)
                {
                    throw RefuseEnrolled(rootAdmission.Root);
                }

                var owner = rootAdmission.Owner;
                using var storeLockHandle = await AcquireStoreLockAsync(cancellationToken).ConfigureAwait(false);
                if (!storeLockHandle.CanProceed)
                {
                    var skipped = LastKnownGoodStartupRecoveryResult.Failed([], LastKnownGoodStartupRecoveryResult.StoreLockUnavailableReason);
                    _startupRecoveryState.MarkFailed(correlationId, skipped.Reason);
                    return skipped;
                }

                IReadOnlyDictionary<string, string> activeVersions;
                using (var borrow = owner.Borrow())
                {
                    var state = await coordinatedRegistry.ReadCoordinatedStateAsync(borrow, cancellationToken)
                        .ConfigureAwait(false);
                    var protection = state.ProtectionRecord
                        ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                            "Participant startup recovery requires a known protected store-state graph.", owner.Root);
                    if (protection.RootIdentity != owner.Root || protection.EnrollmentEpoch != owner.Epoch)
                    {
                        throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                            "The coordinated startup-recovery state differs from its exact admitted root and epoch.", owner.Root);
                    }

                    if (state.ActiveVersionById.Count == 0)
                    {
                        _startupRecoveryState.MarkFailed(correlationId, "no-active-packages");
                        return LastKnownGoodStartupRecoveryResult.Failed([], "no-active-packages");
                    }

                    var verified = PersistedStoreStateGraphVerifier.SelectUseLastKnownGood(state);
                    if (verified.RecoverableGraphs.Count == 0)
                    {
                        throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                            "The verified startup-recovery selection contains no complete graph.", owner.Root);
                    }

                    var descriptors = state.ActivePackageDescriptorsByIdNormalized;
                    var selections = new List<LeasedProtectedRecoveryGraphSelection>(verified.RecoverableGraphs.Count);
                    var packagesById = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);
                    foreach (var graph in verified.RecoverableGraphs)
                    {
                        var graphOwner = await graphAcquisition.AcquireProtectedRecoveryForRootAsync(
                            borrow, graph, configuredInstallRoot, cancellationToken).ConfigureAwait(false);
                        graphUseOwners.Add(graphOwner);

                        var graphPackages = new List<ResolvedPackage>(graph.Nodes.Count);
                        foreach (var node in graph.Nodes)
                        {
                            var install = node.Install;
                            if (!descriptors.TryGetValue(install.PackageId, out var descriptor) ||
                                !string.Equals(descriptor.PackageId, install.PackageId, StringComparison.OrdinalIgnoreCase) ||
                                !string.Equals(descriptor.Version, install.Version, StringComparison.OrdinalIgnoreCase) ||
                                !state.ActiveVersionById.TryGetValue(install.PackageId, out var activeVersion) ||
                                !string.Equals(activeVersion, install.Version, StringComparison.OrdinalIgnoreCase) ||
                                !state.LastKnownGoodById.TryGetValue(install.PackageId, out var lastKnownGoodVersion) ||
                                !string.Equals(lastKnownGoodVersion, install.Version, StringComparison.OrdinalIgnoreCase))
                            {
                                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                                    "The verified recovery graph does not exactly cover the current active, last-known-good descriptor set.", owner.Root);
                            }

                            var exactPath = PackageGraphUseProtectedInstallBinding.GetExactCandidatePath(
                                configuredInstallRoot, install);
                            var package = new ResolvedPackage(install.PackageId, install.Version,
                                descriptor.FeedName ?? string.Empty, exactPath, descriptor.ActivatedAtUtc,
                                descriptor.SourceName ?? string.Empty)
                            {
                                PackageContentHash = install.VerifiedArchiveHash
                            };
                            graphPackages.Add(package);
                            packagesById.TryAdd(package.Id, package);
                        }
                        selections.Add(new LeasedProtectedRecoveryGraphSelection(graph, graphPackages, graphOwner));
                    }

                    recoveredPackages = packagesById.Values
                        .OrderBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(static package => package.Version, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    activeVersions = state.ActiveVersionById.ToDictionary(
                        static item => item.Key,
                        static item => item.Value,
                        StringComparer.OrdinalIgnoreCase);
                    loadingHandoff = new LeasedPackageGraphRecoveryLoadingHandoff(
                        correlationId, activeVersions, selections);

                    var changeSet = new PackageChangeSet([], [], [], correlationId, DateTimeOffset.UtcNow);
                    await scopedDispatcher.PublishReconciledAsync(changeSet, recoveredPackages, owner, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            var loadingResult = _recoveryLoadingObserver is null
                ? new LeasedPackageGraphLoadingResult([])
                : await _recoveryLoadingObserver.LoadRecoveryAsync(
                    loadingHandoff ?? throw new InvalidOperationException("Participant recovery did not create a Loading handoff."),
                    cancellationToken).ConfigureAwait(false);
            if (loadingResult is null)
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                    "The participant recovery loader returned no result.");

            var failedPackageIds = loadingResult.Failures.Select(static failure => failure.PackageId)
                .Concat(TakeLoadFailedPackageIds(correlationId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static packageId => packageId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (failedPackageIds.Length > 0)
            {
                _startupRecoveryState.MarkFailed(correlationId, "last-known-good-load-failed");
                return LastKnownGoodStartupRecoveryResult.Failed(failedPackageIds, "last-known-good-load-failed");
            }

            _startupRecoveryState.MarkRecovered(correlationId, recoveredPackages.Length);
            return new(true, recoveredPackages, [], "last-known-good-recovered");
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            throw;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            for (var index = graphUseOwners.Count - 1; index >= 0; index--)
            {
                try { await graphUseOwners[index].DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }

            if (cleanupFailures.Count > 0)
            {
                if (operationFailure is not null)
                    cleanupFailures.Insert(0, operationFailure);
                throw new AggregateException(
                    "Participant startup recovery completed or failed and one or more graph-use owners did not drain cleanly.",
                    cleanupFailures);
            }
        }
    }

    private async Task<LastKnownGoodStartupRecoveryResult> TryRecoverAfterRootAdmissionAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        // The store lock spans the whole read-then-act cycle below, for the same reason it spans a
        // reconciliation cycle: everything between the read and the republish is part of it. Unlike a
        // reconciliation cycle, recovery waits for a store already owned elsewhere — see
        // AcquireStoreLockAsync — and only reports the skip once that wait times out.
        using var storeLockHandle = await AcquireStoreLockAsync(cancellationToken);
        if (!storeLockHandle.CanProceed)
        {
            var skipped = LastKnownGoodStartupRecoveryResult.Failed([], LastKnownGoodStartupRecoveryResult.StoreLockUnavailableReason);
            _startupRecoveryState.MarkFailed(correlationId, skipped.Reason);
            return skipped;
        }

        if (storeLockHandle.Outcome == StoreLockOutcome.Acquired && _storeRegistry is IStoreStateCycleRefresher stateRefresher)
        {
            await stateRefresher.RefreshFromDiskAsync(cancellationToken);
        }

        var state = await _storeRegistry.GetStateAsync(cancellationToken);
        var activeDescriptors = state.ActivePackageDescriptorsByIdNormalized.Values
            .Where(descriptor => state.ActiveVersionById.ContainsKey(descriptor.PackageId))
            .ToArray();

        // Manual compositions have no configured root to classify. Their selected registry is
        // allowed to provide descriptive state, after which every exact active descriptor path is
        // checked through the ledger-only metadata path before Directory.Exists or an observer.
        if (_installPathAdmission is not null)
        {
            await using var pathAdmission = await _installPathAdmission.AcquireForInstallPathsAsync(
                activeDescriptors.Select(static descriptor => descriptor.InstallPath).ToArray(),
                PackageStoreAdmissionKind.StartupRecovery, cancellationToken).ConfigureAwait(false);
            var enrolledPath = pathAdmission.Entries.FirstOrDefault(static entry =>
                entry.Status == PackageStoreAdmissionStatus.Enrolled);
            if (enrolledPath is not null)
                throw RefuseEnrolled(enrolledPath.Root);
        }

        var validation = Validate(state);
        if (!validation.Succeeded)
        {
            _startupRecoveryState.MarkFailed(correlationId, validation.Reason);
            return validation;
        }

        var recoveredPackages = activeDescriptors
            .Where(descriptor => state.ActiveVersionById.TryGetValue(descriptor.PackageId, out var activeVersion)
                && string.Equals(activeVersion, descriptor.Version, StringComparison.OrdinalIgnoreCase))
            .OrderBy(descriptor => descriptor.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(descriptor => descriptor.Version, StringComparer.OrdinalIgnoreCase)
            .Select(static descriptor => new ResolvedPackage(
                descriptor.PackageId,
                descriptor.Version,
                descriptor.FeedName ?? string.Empty,
                descriptor.InstallPath,
                descriptor.ActivatedAtUtc,
                descriptor.SourceName ?? string.Empty))
            .ToArray();

        var changeSet = new PackageChangeSet([], [], [], correlationId, DateTimeOffset.UtcNow);
        await _observerEventDispatcher.PublishReconciledAsync(changeSet, recoveredPackages, cancellationToken);

        var loadFailedPackageIds = TakeLoadFailedPackageIds(correlationId);
        if (loadFailedPackageIds.Count > 0)
        {
            _startupRecoveryState.MarkFailed(correlationId, "last-known-good-load-failed");
            return LastKnownGoodStartupRecoveryResult.Failed(loadFailedPackageIds, "last-known-good-load-failed");
        }

        _startupRecoveryState.MarkRecovered(correlationId, recoveredPackages.Length);

        return new(true, recoveredPackages, [], "last-known-good-recovered");
    }

    private static PackageStoreAdmissionException RefuseEnrolled(PhysicalRootIdentity? root)
        => new(PackageStoreAdmissionReason.UnsupportedParticipant,
            "Startup LKG recovery requires a live enrolled-root operation and refuses descriptor-path or unscoped-callback fallback.",
            root);

    /// <summary>
    /// Tries once, without waiting, to take the store lock; if that finds it held elsewhere, polls
    /// every <see cref="StoreLockPollInterval"/> until it is acquired or the configured
    /// <see cref="ReconciliationOptions.StartupRecoveryStoreLockTimeout"/> elapses. A store that cannot
    /// be locked at all (<see cref="StoreLockOutcome.NotLockable"/>) is not contention, so it is never
    /// waited on: the first attempt's handle already has <see cref="StoreLockHandle.CanProceed"/>
    /// <see langword="true"/> and is returned immediately, the same as when there is no store lock to
    /// take at all.
    /// </summary>
    private async Task<StoreLockHandle> AcquireStoreLockAsync(CancellationToken cancellationToken)
    {
        if (_storeLock is null)
        {
            return StoreLockHandle.NotRequired();
        }

        var handle = _storeLock.Acquire();
        if (handle.CanProceed || _storeLockTimeout <= TimeSpan.Zero)
        {
            return handle;
        }

        var deadline = DateTimeOffset.UtcNow + _storeLockTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(StoreLockPollInterval, cancellationToken);

            handle = _storeLock.Acquire();
            if (handle.CanProceed)
            {
                return handle;
            }
        }

        return handle;
    }

    private static LastKnownGoodStartupRecoveryResult Validate(StoreStateRecord state)
    {
        if (state.ActiveVersionById.Count == 0)
        {
            return LastKnownGoodStartupRecoveryResult.Failed([], "no-active-packages");
        }

        var failed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (packageId, activeVersion) in state.ActiveVersionById)
        {
            if (!state.LastKnownGoodById.TryGetValue(packageId, out var lkgVersion) ||
                !string.Equals(lkgVersion, activeVersion, StringComparison.OrdinalIgnoreCase))
            {
                failed.Add(packageId);
                continue;
            }

            if (!state.ActivePackageDescriptorsByIdNormalized.TryGetValue(packageId, out var descriptor) ||
                !string.Equals(descriptor.Version, activeVersion, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(descriptor.InstallPath))
            {
                failed.Add(packageId);
            }
        }

        foreach (var graph in state.ActiveGraphsByIdNormalized.Values.Where(static graph => graph.Status == GraphActivationStatus.Active))
        {
            foreach (var nodePackageId in graph.NodePackageIds)
            {
                if (!state.ActiveVersionById.ContainsKey(nodePackageId))
                {
                    failed.Add(nodePackageId);
                }
            }

            if (graph.NodeVersionsByPackageId is null)
            {
                continue;
            }

            foreach (var (nodePackageId, nodeVersion) in graph.NodeVersionsByPackageId)
            {
                if (!state.ActiveVersionById.TryGetValue(nodePackageId, out var activeVersion) ||
                    !string.Equals(activeVersion, nodeVersion, StringComparison.OrdinalIgnoreCase))
                {
                    failed.Add(nodePackageId);
                }
            }
        }

        return failed.Count == 0
            ? new(true, [], [], "last-known-good-valid")
            : LastKnownGoodStartupRecoveryResult.Failed(failed.ToArray(), "last-known-good-invalid");
    }

    private IReadOnlyList<string> TakeLoadFailedPackageIds(string correlationId) =>
        _cycleFailureContributors
            .SelectMany(contributor => contributor.TakeFailedPackageIds(correlationId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
