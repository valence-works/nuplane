using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.State;

namespace Nuplane.Store.Coordination.ProtectionRecords;

/// <summary>Creates digest-bound v1 protection metadata for a state candidate under quiescent ownership.</summary>
internal static class PackageStoreProtectionRecordFactory
{
    internal static StoreStateRecord WithProtection(
        StoreStateRecord state,
        PhysicalRootIdentity root,
        long epoch,
        string memberId,
        long revision,
        PackageProtectionClosure activeClosure,
        PackageProtectionClosure recoverableClosure,
        IEnumerable<RetiredGraphEvidence>? retiredGraphs = null,
        bool legacyUnknownRecovery = false,
        LegacyRecoveryRetirementEvidence? legacyRecoveryRetirementEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberId);
        if (epoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(epoch));
        ArgumentNullException.ThrowIfNull(activeClosure);
        ArgumentNullException.ThrowIfNull(recoverableClosure);

        var effectiveLegacyRetirement = legacyRecoveryRetirementEvidence ?? state.ProtectionRecord?.LegacyRecoveryRetirementEvidence;
        var candidate = new PackageProtectionRecord(PackageProtectionRecord.CurrentSchemaVersion,
            root, epoch, memberId, revision, ProtectionDigest.StateBody(state), new string('0', 64),
            activeClosure, recoverableClosure, retiredGraphs ?? [], legacyUnknownRecovery, effectiveLegacyRetirement);
        return state with
        {
            ProtectionRecord = new PackageProtectionRecord(candidate.SchemaVersion, candidate.RootIdentity,
                candidate.EnrollmentEpoch, candidate.MemberId, candidate.Revision, candidate.StateBodyDigest,
                ProtectionDigest.Protection(candidate), candidate.ActiveClosure, candidate.RecoverableClosure,
                candidate.RetiredGraphs, candidate.LegacyUnknownRecovery, candidate.LegacyRecoveryRetirementEvidence),
            ProtectionBundle = null
        };
    }
}
