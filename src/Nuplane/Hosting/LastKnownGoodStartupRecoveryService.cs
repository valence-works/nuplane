using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Reconciliation.Configuration;
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
            storeLock: null)
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
    internal LastKnownGoodStartupRecoveryService(
        IStoreRegistry storeRegistry,
        IObserverEventDispatcher observerEventDispatcher,
        StartupRecoveryState startupRecoveryState,
        IEnumerable<ICycleFailureContributor>? cycleFailureContributors,
        IOptions<ReconciliationOptions>? reconciliationOptions,
        IStoreLock? storeLock)
    {
        _storeRegistry = storeRegistry ?? throw new ArgumentNullException(nameof(storeRegistry));
        _observerEventDispatcher = observerEventDispatcher ?? throw new ArgumentNullException(nameof(observerEventDispatcher));
        _startupRecoveryState = startupRecoveryState ?? throw new ArgumentNullException(nameof(startupRecoveryState));
        _cycleFailureContributors = cycleFailureContributors?.ToArray() ?? [];
        _storeLockTimeout = (reconciliationOptions?.Value ?? new ReconciliationOptions()).StartupRecoveryStoreLockTimeout;
        _storeLock = storeLock;
    }

    public async Task<LastKnownGoodStartupRecoveryResult> TryRecoverAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

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

        var state = await _storeRegistry.GetStateAsync(cancellationToken);
        var validation = Validate(state);
        if (!validation.Succeeded)
        {
            _startupRecoveryState.MarkFailed(correlationId, validation.Reason);
            return validation;
        }

        var recoveredPackages = state.ActivePackageDescriptorsByIdNormalized.Values
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
