using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Observability;
using Nuplane.Store.Coordination;
using Nuplane.Store.State;

namespace Nuplane.Operational;

/// <summary>
/// Default runtime implementation of <see cref="IActivePackageCatalog"/>.
/// Reads the persisted active package descriptor set without replaying observer history.
/// </summary>
public sealed class ActivePackageCatalog : IActivePackageCatalog
{
    private readonly IStoreRegistry _storeRegistry;
    private readonly IReconciliationLogger _logger;
    private readonly ReconciliationMetrics _metrics;
    private readonly IPackageStoreAdmission? _packageStoreAdmission;

    /// <summary>Initializes the ordinary active catalog reader.</summary>
    /// <param name="storeRegistry">The registry used by the ordinary, uncoordinated read path.</param>
    /// <param name="logger">The reconciliation logger used to record the catalog read.</param>
    /// <param name="metrics">The metrics recorder used to record the catalog read.</param>
    public ActivePackageCatalog(
        IStoreRegistry storeRegistry,
        IReconciliationLogger logger,
        ReconciliationMetrics metrics)
        : this(storeRegistry, logger, metrics, packageStoreAdmission: null)
    {
    }

    internal ActivePackageCatalog(
        IStoreRegistry storeRegistry,
        IReconciliationLogger logger,
        ReconciliationMetrics metrics,
        IPackageStoreAdmission? packageStoreAdmission)
    {
        _storeRegistry = storeRegistry ?? throw new ArgumentNullException(nameof(storeRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _packageStoreAdmission = packageStoreAdmission;
    }

    /// <inheritdoc />
    public async Task<ActivePackagesSnapshot> GetActivePackagesAsync(CancellationToken cancellationToken)
    {
        var correlationId = CorrelationContext.CreateNew();
        var state = await ReadCurrentStateAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = ActivePackageCatalogMapper.MapSnapshot(state, correlationId);

        var issues = CalculateDescriptorIssues(state);
        _logger.LogActivePackageCatalogRead(correlationId, snapshot.Packages.Count, issues);
        _metrics.RecordActivePackageCatalogRead(snapshot.Packages.Count, issues > 0);

        return snapshot;
    }

    /// <summary>
    /// Reads the current active package catalog snapshot using legacy naming.
    /// </summary>
    public async Task<ActivePackageCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        (await GetActivePackagesAsync(cancellationToken).ConfigureAwait(false)).ToLegacySnapshot();

    private async Task<StoreStateRecord> ReadCurrentStateAsync(CancellationToken cancellationToken)
    {
        if (_packageStoreAdmission is null)
            return await _storeRegistry.GetStateAsync(cancellationToken).ConfigureAwait(false);

        await using var admission = await _packageStoreAdmission
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance, cancellationToken)
            .ConfigureAwait(false);
        if (admission.Status != PackageStoreAdmissionStatus.Enrolled)
            throw Refuse(PackageStoreAdmissionReason.UnsupportedParticipant,
                "The configured package-store member is not enrolled; active catalog reads cannot use the ordinary route.", admission.Root);

        var owner = admission.Owner
            ?? throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "Enrolled configured-root admission did not retain an operation owner.", admission.Root);
        if (admission.Root is null || admission.Root != owner.Root)
            throw Refuse(PackageStoreAdmissionReason.RootMismatch,
                "Configured-root admission and its operation owner identify different roots.", admission.Root);
        if (_storeRegistry is not ICoordinatedStoreRegistry coordinatedRegistry)
            throw Refuse(PackageStoreAdmissionReason.UnsupportedParticipant,
                "The configured state registry has no coordinated read contract.", admission.Root);

        using var borrow = owner.Borrow();
        return await coordinatedRegistry.ReadCoordinatedStateAsync(borrow, cancellationToken).ConfigureAwait(false);
    }

    private static PackageStoreAdmissionException Refuse(
        PackageStoreAdmissionReason reason,
        string message,
        PhysicalRootIdentity? root = null) => new(reason, message, root);

    private static int CalculateDescriptorIssues(StoreStateRecord state)
    {
        var issues = 0;
        foreach (var (packageId, version) in state.ActiveVersionById)
        {
            if (!state.ActivePackageDescriptorsByIdNormalized.TryGetValue(packageId, out var descriptor) ||
                !string.Equals(descriptor.Version, version, StringComparison.OrdinalIgnoreCase))
            {
                issues++;
            }
        }

        return issues;
    }
}

