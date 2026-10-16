using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Operational;
using Nuplane.Registration;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;

namespace Nuplane;

/// <summary>Participant-composition-bound access used by <see cref="NuplaneStore.OpenActivePackageSnapshotAsync"/>.</summary>
/// <remarks>The constructor is internal; AddNuplane registers this only for a validated coordinated composition.</remarks>
public sealed class NuplaneStoreSnapshotAccess
{
    private readonly StoreRegistry _registry;
    private readonly StoreStateSerializer _serializer;
    private readonly PackageStoreAdmission _admission;
    private readonly PackageStoreCompositionSnapshot _composition;
    private readonly PackageGraphUseLeaseAcquisition _graphUseAcquisition;

    internal NuplaneStoreSnapshotAccess(
        StoreRegistry registry,
        StoreStateSerializer serializer,
        PackageStoreAdmission admission,
        PackageStoreCompositionSnapshot composition,
        PackageGraphUseLeaseAcquisition graphUseAcquisition,
        IPackageGraphUseLifetimeObserver lifetimeObserver,
        IResolvedPackageGraphUseLeaseAcquisition selectedGraphUseAcquisition)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(graphUseAcquisition);
        ArgumentNullException.ThrowIfNull(lifetimeObserver);
        ArgumentNullException.ThrowIfNull(selectedGraphUseAcquisition);
        if (!ReferenceEquals(registry.PayloadSerializer, serializer) ||
            !ReferenceEquals(admission.SelectedStoreRegistry, registry) ||
            !composition.IsParticipant ||
            !ReferenceEquals(admission.Registry, graphUseAcquisition.Registry) ||
            !ReferenceEquals(admission.Files, graphUseAcquisition.Registry?.Files) ||
            !ReferenceEquals(graphUseAcquisition.LifetimeObserver, lifetimeObserver) ||
            !ReferenceEquals(graphUseAcquisition, selectedGraphUseAcquisition))
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "Snapshot access requires the native admission, participant composition, lease publisher and lifetime observer selected by one composition.");
        }

        _registry = registry;
        _serializer = serializer;
        _admission = admission;
        _composition = composition;
        _graphUseAcquisition = graphUseAcquisition;
    }

    internal PackageStoreCompositionSnapshot Composition => _composition;

    internal ValueTask<PackageGraphUseLeaseOwner> AcquireProtectedActiveGraphAsync(
        PackageStoreOperationBorrow borrow,
        ProtectedGraphSnapshot activeGraph,
        IReadOnlyDictionary<Guid, string> exactPathsByNode,
        CancellationToken cancellationToken) =>
        _graphUseAcquisition.AcquireProtectedActiveForRootAsync(borrow, activeGraph, exactPathsByNode, cancellationToken);

    internal async Task<AdmittedActivePackageSnapshot> OpenActivePackageSnapshotAsync(
        CancellationToken cancellationToken)
        => await OpenActivePackageSnapshotAsync(PackageStoreAdmissionKind.Maintenance, cancellationToken)
            .ConfigureAwait(false);

    internal async Task<AdmittedActivePackageSnapshot> OpenActivePackageSnapshotForLoadingAsync(
        CancellationToken cancellationToken)
        => await OpenActivePackageSnapshotAsync(PackageStoreAdmissionKind.Loading, cancellationToken)
            .ConfigureAwait(false);

    private async Task<AdmittedActivePackageSnapshot> OpenActivePackageSnapshotAsync(
        PackageStoreAdmissionKind kind,
        CancellationToken cancellationToken)
    {
        var statePath = _registry.CoordinatedStateFileLocator;
        if (string.IsNullOrWhiteSpace(statePath))
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "An admitted active snapshot requires the selected coordinated state member.");

        var initialState = await NuplaneStore.ReadStateAsync(statePath, cancellationToken).ConfigureAwait(false);
        return await OpenObservedActivePackageSnapshotAsync(initialState, kind, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<AdmittedActivePackageSnapshot> OpenObservedActivePackageSnapshotAsync(
        StoreStateRecord observedState,
        CancellationToken cancellationToken)
        => await OpenObservedActivePackageSnapshotAsync(observedState, PackageStoreAdmissionKind.Maintenance, cancellationToken)
            .ConfigureAwait(false);

    private async Task<AdmittedActivePackageSnapshot> OpenObservedActivePackageSnapshotAsync(
        StoreStateRecord observedState,
        PackageStoreAdmissionKind kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observedState);
        // Copy the observed state's dictionaries before the first await so caller-side map edits
        // cannot change the comparison during admission.
        var initialState = StoreRegistry.CopyState(observedState);
        var initialSnapshot = ActivePackageCatalogMapper.MapSnapshot(initialState, Guid.NewGuid().ToString("N"));
        var installPaths = initialSnapshot.Packages.Select(static package => package.InstallPath)
            .Distinct(StringComparer.Ordinal).ToArray();
        var admission = await _admission.AcquireForActiveSnapshotAsync(installPaths, cancellationToken, kind)
            .ConfigureAwait(false);

        try
        {
            var initialPayload = await FingerprintStateAsync(initialState, cancellationToken).ConfigureAwait(false);
            var admittedPayload = await FingerprintStateAsync(admission.State, cancellationToken).ConfigureAwait(false);
            var admittedProjection = ActivePackageCatalogMapper.MapActivePackages(admission.State);
            if (!CryptographicOperations.FixedTimeEquals(initialPayload, admittedPayload) ||
                !SameProtectionRevision(initialState, admission.State) ||
                !SameProjection(initialSnapshot.Packages, admittedProjection))
            {
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                    "The selected store state or active descriptor projection changed while the snapshot was admitted.");
            }

            if (admission.Paths.Entries.Any(static entry => entry.Status != PackageStoreAdmissionStatus.Enrolled))
            {
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                    "An active package path is outside the selected trusted participant catalog.");
            }

            var immutableSnapshot = CopySnapshot(initialSnapshot);
            return new AdmittedActivePackageSnapshot(immutableSnapshot, admission.Paths,
                StoreRegistry.CopyState(admission.State));
        }
        catch (Exception primaryFailure)
        {
            try
            {
                await admission.Paths.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Active snapshot verification failed and its retained path owners did not release cleanly.",
                    primaryFailure, cleanupFailure);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            throw;
        }
    }

    private async Task<byte[]> FingerprintStateAsync(StoreStateRecord state, CancellationToken cancellationToken)
    {
        // StoreStateRecord.Empty() timestamps each observation. Once every persisted field is empty,
        // that generated timestamp is not part of the state-file binding being compared.
        if (IsSemanticallyEmpty(state))
            state = state with { UpdatedAt = DateTimeOffset.UnixEpoch };

        await using var payload = new MemoryStream();
        await _serializer.WritePayloadAsync(payload, state, cancellationToken).ConfigureAwait(false);
        return SHA256.HashData(payload.GetBuffer().AsSpan(0, checked((int)payload.Length)));
    }

    private static bool IsSemanticallyEmpty(StoreStateRecord state) =>
        state.ActiveVersionById.Count == 0 && state.LastKnownGoodById.Count == 0 &&
        state.LastFailureById.Count == 0 && state.LastSuccessfulSourceSnapshots.Count == 0 &&
        state.ActivePackageDescriptorsByIdNormalized.Count == 0 && state.ActiveGraphsByIdNormalized.Count == 0 &&
        state.ProtectionRecord is null && state.ProtectionBundle is null;

    private static bool SameProtectionRevision(StoreStateRecord left, StoreStateRecord right)
    {
        var leftRecord = left.ProtectionRecord;
        var rightRecord = right.ProtectionRecord;
        if (leftRecord?.Revision != rightRecord?.Revision ||
            !string.Equals(leftRecord?.StateBodyDigest, rightRecord?.StateBodyDigest, StringComparison.Ordinal) ||
            !string.Equals(leftRecord?.ProtectionDigest, rightRecord?.ProtectionDigest, StringComparison.Ordinal))
            return false;

        var leftBundle = left.ProtectionBundle;
        var rightBundle = right.ProtectionBundle;
        return leftBundle?.StateGeneration == rightBundle?.StateGeneration &&
               string.Equals(leftBundle?.StateBodyDigest, rightBundle?.StateBodyDigest, StringComparison.Ordinal) &&
               leftBundle?.PublicationId == rightBundle?.PublicationId &&
               string.Equals(leftBundle?.ParticipantSetDigest, rightBundle?.ParticipantSetDigest, StringComparison.Ordinal);
    }

    private static bool SameProjection(IReadOnlyList<ActivePackage> left, IReadOnlyList<ActivePackage> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            var first = left[index];
            var second = right[index];
            if (!string.Equals(first.PackageId, second.PackageId, StringComparison.Ordinal) ||
                !string.Equals(first.Version, second.Version, StringComparison.Ordinal) ||
                !string.Equals(first.InstallPath, second.InstallPath, StringComparison.Ordinal) ||
                !string.Equals(first.FeedName, second.FeedName, StringComparison.Ordinal) ||
                !string.Equals(first.SourceName, second.SourceName, StringComparison.Ordinal) ||
                first.ActivatedAtUtc != second.ActivatedAtUtc ||
                !string.Equals(first.ActivationCorrelationId, second.ActivationCorrelationId, StringComparison.Ordinal) ||
                !string.Equals(first.GraphId, second.GraphId, StringComparison.Ordinal) ||
                !string.Equals(first.GraphGenerationId, second.GraphGenerationId, StringComparison.Ordinal) ||
                first.PackageRole != second.PackageRole || first.Discoverable != second.Discoverable ||
                !first.RootPackageIds.SequenceEqual(second.RootPackageIds, StringComparer.Ordinal) ||
                !first.DependencyOfPackageIds.SequenceEqual(second.DependencyOfPackageIds, StringComparer.Ordinal))
                return false;
        }

        return true;
    }

    private static ActivePackagesSnapshot CopySnapshot(ActivePackagesSnapshot source)
    {
        var packages = source.Packages.Select(static package => package with
        {
            RootPackageIds = Array.AsReadOnly(package.RootPackageIds.ToArray()),
            DependencyOfPackageIds = Array.AsReadOnly(package.DependencyOfPackageIds.ToArray())
        }).ToArray();
        return source with { Packages = new ReadOnlyCollection<ActivePackage>(packages) };
    }
}
