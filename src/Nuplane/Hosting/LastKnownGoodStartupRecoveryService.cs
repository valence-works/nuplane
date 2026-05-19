using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Store.State;

namespace Nuplane.Hosting;

internal sealed class LastKnownGoodStartupRecoveryService(
    IStoreRegistry storeRegistry,
    IObserverEventDispatcher observerEventDispatcher,
    StartupRecoveryState startupRecoveryState,
    IEnumerable<ICycleFailureContributor>? cycleFailureContributors = null) : ILastKnownGoodStartupRecoveryService
{
    private readonly IStoreRegistry _storeRegistry = storeRegistry ?? throw new ArgumentNullException(nameof(storeRegistry));
    private readonly IObserverEventDispatcher _observerEventDispatcher = observerEventDispatcher ?? throw new ArgumentNullException(nameof(observerEventDispatcher));
    private readonly StartupRecoveryState _startupRecoveryState = startupRecoveryState ?? throw new ArgumentNullException(nameof(startupRecoveryState));
    private readonly IReadOnlyList<ICycleFailureContributor> _cycleFailureContributors = cycleFailureContributors?.ToArray() ?? [];

    public async Task<LastKnownGoodStartupRecoveryResult> TryRecoverAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

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
