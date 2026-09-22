using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Store.State;

namespace Nuplane.Hosting;

/// <summary>
/// Recovers a degraded startup cycle by re-activating the last-known-good package set recorded in
/// the store.
/// </summary>
/// <remarks>
/// Recovery reads the store and, on success, republishes the last-known-good packages as reconciled
/// — the same read-then-act shape as a reconciliation cycle. It therefore takes the same
/// cross-process <see cref="IStoreLock"/> a reconciliation cycle takes, for the same reason: without
/// it, a startup recovery could act on a state file a concurrent <c>NuplaneRestore</c> run or a
/// second host process is mid-write on. When the lock cannot be taken, recovery does nothing —
/// no read, no republish — and reports <see cref="LastKnownGoodStartupRecoveryResult.StoreLockUnavailableReason"/>
/// instead. A host configured with <c>StartupFailurePolicy.UseLastKnownGood</c> should treat that the
/// same as any other failed recovery: the startup cycle fails over to whatever
/// <c>StartupFailurePolicy</c> would otherwise do, and a subsequent automatic reconciliation cycle
/// (if enabled) retries.
/// </remarks>
internal sealed class LastKnownGoodStartupRecoveryService(
    IStoreRegistry storeRegistry,
    IObserverEventDispatcher observerEventDispatcher,
    StartupRecoveryState startupRecoveryState,
    IEnumerable<ICycleFailureContributor>? cycleFailureContributors = null,
    IStoreLock? storeLock = null) : ILastKnownGoodStartupRecoveryService
{
    private readonly IStoreRegistry _storeRegistry = storeRegistry ?? throw new ArgumentNullException(nameof(storeRegistry));
    private readonly IObserverEventDispatcher _observerEventDispatcher = observerEventDispatcher ?? throw new ArgumentNullException(nameof(observerEventDispatcher));
    private readonly StartupRecoveryState _startupRecoveryState = startupRecoveryState ?? throw new ArgumentNullException(nameof(startupRecoveryState));
    private readonly IReadOnlyList<ICycleFailureContributor> _cycleFailureContributors = cycleFailureContributors?.ToArray() ?? [];
    private readonly IStoreLock? _storeLock = storeLock;

    public async Task<LastKnownGoodStartupRecoveryResult> TryRecoverAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        // The store lock spans the whole read-then-act cycle below, for the same reason it spans a
        // reconciliation cycle: everything between the read and the republish is part of it. A store
        // already owned elsewhere yields a reported skip rather than acting on a file another writer
        // may be mid-rewrite on.
        using var storeLockHandle = _storeLock?.Acquire() ?? StoreLockHandle.NotRequired();
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
