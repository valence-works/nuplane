using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.State;

public sealed partial class StoreRegistry
{
    /// <inheritdoc />
    public async Task<StoreStateRecord> ReadCoordinatedStateAsync(
        PackageStoreOperationBorrow borrow,
        CancellationToken cancellationToken)
    {
        RequireCoordinatedPersistence();
        ArgumentNullException.ThrowIfNull(borrow);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = PackageStoreOperationAccess.GetLockedMemberLocations(borrow);
            context.RequirePayloadSerializer(_serializer);
            var state = await context.ReadConfiguredStateAsync(_coordinatedStateFileLocator!, cancellationToken).ConfigureAwait(false);
            _currentState = CopyState(state);
            _loaded = true;
            return CopyState(state);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public Task PersistCoordinatedFailureAsync(
        PackageStoreOperationBorrow borrow,
        string packageId,
        string stage,
        string message,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        return MutateCoordinatedStateAsync(borrow, state =>
        {
            var failures = new Dictionary<string, FailureRecord>(state.LastFailureById, StringComparer.OrdinalIgnoreCase)
            {
                [packageId] = new(packageId, stage, message, DateTimeOffset.UtcNow, correlationId)
            };
            return ProtectRegistryMutation(state with
            {
                LastFailureById = failures,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task PersistCoordinatedSourceSnapshotAsync(
        PackageStoreOperationBorrow borrow,
        string sourceName,
        SourceSnapshotRef snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(snapshot);

        return MutateCoordinatedStateAsync(borrow, state =>
        {
            var snapshots = new Dictionary<string, SourceSnapshotRef>(state.LastSuccessfulSourceSnapshots,
                StringComparer.OrdinalIgnoreCase)
            {
                [sourceName] = snapshot
            };
            return ProtectRegistryMutation(state with
            {
                LastSuccessfulSourceSnapshots = snapshots,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task PersistCoordinatedActiveStateAsync(
        PackageStoreOperationBorrow borrow,
        StoreStateRecord completeNextState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        ArgumentNullException.ThrowIfNull(completeNextState);
        var candidate = CopyState(completeNextState);
        RefuseLegacyBundleMutation(candidate);
        await MutateCoordinatedStateAsync(borrow, _ => candidate, cancellationToken).ConfigureAwait(false);
    }

    private async Task MutateCoordinatedStateAsync(
        PackageStoreOperationBorrow borrow,
        Func<StoreStateRecord, StoreStateRecord> createNextState,
        CancellationToken cancellationToken)
    {
        RequireCoordinatedPersistence();
        ArgumentNullException.ThrowIfNull(borrow);
        ArgumentNullException.ThrowIfNull(createNextState);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = PackageStoreOperationAccess.GetLockedMemberLocations(borrow);
            context.RequirePayloadSerializer(_serializer);
            var nextState = await context.MutateConfiguredStateAsync(
                _coordinatedStateFileLocator!, state =>
                {
                    RefuseLegacyBundleMutation(state);
                    var candidate = createNextState(state)
                        ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                            "A coordinated state mutation did not provide a candidate.");
                    RefuseLegacyBundleMutation(candidate);
                    return candidate;
                }, cancellationToken).ConfigureAwait(false);
            _currentState = CopyState(nextState);
            _loaded = true;
        }
        catch
        {
            _loaded = false;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RequireCoordinatedPersistence()
    {
        if (string.IsNullOrWhiteSpace(_stateFilePath) || string.IsNullOrWhiteSpace(_coordinatedStateFileLocator))
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "Coordinated state access requires one configured on-disk state slot.");
        if (_serializer is not IPackageProtectionStatePayloadSerializer)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "Coordinated state access requires a caller-stream protection serializer.");
    }

    internal static StoreStateRecord ProtectRegistryMutation(StoreStateRecord state)
    {
        RefuseLegacyBundleMutation(state);
        var prior = state.ProtectionRecord
            ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                "A coordinated state mutation requires an acknowledged protection record.");
        long revision;
        try { revision = checked(prior.Revision + 1); }
        catch (OverflowException exception)
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                "The coordinated state protection revision cannot advance.", prior.RootIdentity, exception);
        }

        var stateDigest = ProtectionDigest.StateBody(state);
        var candidate = new PackageProtectionRecord(
            prior.SchemaVersion,
            prior.RootIdentity,
            prior.EnrollmentEpoch,
            prior.MemberId,
            revision,
            stateDigest,
            new string('0', 64),
            prior.ActiveClosure,
            prior.RecoverableClosure,
            prior.RetiredGraphs,
            prior.LegacyUnknownRecovery);
        var protection = new PackageProtectionRecord(
            candidate.SchemaVersion,
            candidate.RootIdentity,
            candidate.EnrollmentEpoch,
            candidate.MemberId,
            candidate.Revision,
            candidate.StateBodyDigest,
            ProtectionDigest.Protection(candidate),
            candidate.ActiveClosure,
            candidate.RecoverableClosure,
            candidate.RetiredGraphs,
            candidate.LegacyUnknownRecovery);
        return state with { ProtectionRecord = protection };
    }

    private static StoreStateRecord CopyState(StoreStateRecord state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new StoreStateRecord(
            new Dictionary<string, string>(state.ActiveVersionById, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(state.LastKnownGoodById, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, FailureRecord>(state.LastFailureById, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SourceSnapshotRef>(state.LastSuccessfulSourceSnapshots, StringComparer.OrdinalIgnoreCase),
            state.UpdatedAt,
            new Dictionary<string, ActivePackageDescriptor>(state.ActivePackageDescriptorsByIdNormalized,
                StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, GraphActivationRecord>(state.ActiveGraphsByIdNormalized, StringComparer.OrdinalIgnoreCase))
        {
            ProtectionRecord = state.ProtectionRecord,
            ProtectionBundle = state.ProtectionBundle?.Copy()
        };
    }

    private static void RefuseLegacyBundleMutation(StoreStateRecord state)
    {
        if (state.ProtectionBundle is not null)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "The single-root coordinated registry cannot mutate a v2 protection bundle without its group owner.");
    }
}
