using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Coordination.MembershipRecords;

/// <summary>Describes the prior member binding omitted from a completed membership set.</summary>
/// <remarks>
/// The proof digest is a structural reference only. Runtime code must independently verify that
/// the member's complete active and recoverable protection was migrated or explicitly retired.
/// </remarks>
internal sealed class RootMemberRetirementEvidence
{
    internal RootMemberRetirementEvidence(
        string memberId,
        RootMemberRecord.AcknowledgedBinding priorBinding,
        long retiringEpoch,
        string proofDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberId);
        ArgumentNullException.ThrowIfNull(priorBinding);
        if (retiringEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(retiringEpoch));
        ArgumentException.ThrowIfNullOrWhiteSpace(proofDigest);

        MemberId = memberId;
        PriorBinding = (RootMemberRecord.AcknowledgedBinding)priorBinding.Copy();
        RetiringEpoch = retiringEpoch;
        ProofDigest = proofDigest;
    }

    internal string MemberId { get; }

    internal RootMemberRecord.AcknowledgedBinding PriorBinding { get; }

    internal long RetiringEpoch { get; }

    internal string ProofDigest { get; }

    internal RootMemberRetirementEvidence Copy()
        => new(MemberId, PriorBinding, RetiringEpoch, ProofDigest);
}
