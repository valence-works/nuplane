using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;

namespace Nuplane.Store.Coordination.MembershipRecords;

/// <summary>One root-local copy of an immutable group intent and its monotonic durable phase.</summary>
internal sealed class PendingGroupPublicationV2
{
    internal PendingGroupPublicationV2(
        GroupPublicationDescriptorV2 descriptor,
        PhysicalRootIdentity rootIdentity,
        GroupPublicationPhaseV2 phase,
        PhysicalFileIdentity? stagedStateFileIdentity,
        PhysicalFileIdentity? backupStateFileIdentity,
        GroupPublicationResolutionV2 resolution,
        string? expectedBoundCommitDigest = null,
        string? expectedResolutionDigest = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(rootIdentity);
        if (!Enum.IsDefined(phase))
            throw new ArgumentOutOfRangeException(nameof(phase));
        if (!Enum.IsDefined(resolution))
            throw new ArgumentOutOfRangeException(nameof(resolution));

        Descriptor = descriptor.Copy();
        LocalParticipant = Descriptor.GetParticipant(rootIdentity);
        RootIdentity = MembershipRecordValueCopies.CopyRoot(rootIdentity);

        if (phase == GroupPublicationPhaseV2.Intent)
        {
            if (stagedStateFileIdentity is not null || backupStateFileIdentity is not null ||
                resolution != GroupPublicationResolutionV2.Unresolved || expectedBoundCommitDigest is not null ||
                expectedResolutionDigest is not null)
                throw new ArgumentException("An intent-only group record cannot contain artifacts or a resolution.", nameof(phase));
        }

        if (phase == GroupPublicationPhaseV2.ArtifactsBound)
        {
            if (stagedStateFileIdentity is null || resolution != GroupPublicationResolutionV2.Unresolved || expectedResolutionDigest is not null)
                throw new ArgumentException("An artifacts-bound group record requires a stage and no resolution.", nameof(phase));
            RequireArtifactPair(stagedStateFileIdentity, backupStateFileIdentity);
        }

        if (phase == GroupPublicationPhaseV2.Resolved)
        {
            if (resolution == GroupPublicationResolutionV2.Unresolved)
                throw new ArgumentException("A resolved group record requires Prior or Next.", nameof(resolution));
            if (expectedResolutionDigest is null)
                throw new ArgumentException("A resolved group record requires its resolution digest.", nameof(expectedResolutionDigest));
            if (stagedStateFileIdentity is null)
            {
                if (backupStateFileIdentity is not null || expectedBoundCommitDigest is not null ||
                    resolution != GroupPublicationResolutionV2.Prior)
                    throw new ArgumentException("Only an artifact-free Prior resolution may omit the bound phase.", nameof(stagedStateFileIdentity));
            }
            else
            {
                RequireArtifactPair(stagedStateFileIdentity, backupStateFileIdentity);
            }
            if (resolution == GroupPublicationResolutionV2.Next && stagedStateFileIdentity is null)
                throw new ArgumentException("Next requires the exact artifacts-bound phase.", nameof(resolution));
        }

        if (stagedStateFileIdentity is not null)
        {
            var boundCommitDigest = expectedBoundCommitDigest
                ?? ProtectionDigest.GroupPublicationBoundV2(Descriptor, stagedStateFileIdentity, backupStateFileIdentity);
            ProtectionDigest.ValidateCanonicalDigest(boundCommitDigest);
            var actualBoundDigest = ProtectionDigest.GroupPublicationBoundV2(Descriptor, stagedStateFileIdentity, backupStateFileIdentity);
            if (!string.Equals(boundCommitDigest, actualBoundDigest, StringComparison.Ordinal))
                throw new ArgumentException("The bound-commit digest does not match the observed artifacts.", nameof(expectedBoundCommitDigest));
            BoundCommitDigest = boundCommitDigest;
        }
        else if (expectedBoundCommitDigest is not null)
        {
            throw new ArgumentException("A group record without bound artifacts cannot carry a bound digest.", nameof(expectedBoundCommitDigest));
        }

        if (phase == GroupPublicationPhaseV2.Resolved)
        {
            var resolutionDigest = ProtectionDigest.GroupPublicationDecisionV2(Descriptor,
                BoundCommitDigest, resolution);
            ProtectionDigest.ValidateCanonicalDigest(expectedResolutionDigest!);
            if (!string.Equals(resolutionDigest, expectedResolutionDigest, StringComparison.Ordinal))
                throw new ArgumentException("The resolution digest does not match the immutable group decision.", nameof(expectedResolutionDigest));
            ResolutionDigest = resolutionDigest;
        }

        Phase = phase;
        StagedStateFileIdentity = stagedStateFileIdentity is null ? null : MembershipRecordValueCopies.CopyIdentity(stagedStateFileIdentity);
        BackupStateFileIdentity = backupStateFileIdentity is null ? null : MembershipRecordValueCopies.CopyIdentity(backupStateFileIdentity);
        Resolution = resolution;
    }

    internal GroupPublicationDescriptorV2 Descriptor { get; }
    internal GroupPublicationParticipantV2 LocalParticipant { get; }
    internal PhysicalRootIdentity RootIdentity { get; }
    internal GroupPublicationPhaseV2 Phase { get; }
    internal PhysicalFileIdentity? StagedStateFileIdentity { get; }
    internal PhysicalFileIdentity? BackupStateFileIdentity { get; }
    internal string? BoundCommitDigest { get; }
    internal GroupPublicationResolutionV2 Resolution { get; }
    internal string? ResolutionDigest { get; }

    internal PendingGroupPublicationV2 BindArtifacts(PhysicalFileIdentity staged, PhysicalFileIdentity? backup)
    {
        ArgumentNullException.ThrowIfNull(staged);
        if (Phase == GroupPublicationPhaseV2.Resolved)
            throw new InvalidOperationException("A resolved group publication cannot return to an artifacts-bound phase.");
        if (Phase == GroupPublicationPhaseV2.ArtifactsBound)
        {
            if (StagedStateFileIdentity == staged && BackupStateFileIdentity == backup)
                return Copy();
            throw new InvalidOperationException("A group publication cannot rebind artifact identities.");
        }
        return new(Descriptor, RootIdentity, GroupPublicationPhaseV2.ArtifactsBound, staged, backup,
            GroupPublicationResolutionV2.Unresolved);
    }

    internal PendingGroupPublicationV2 Resolve(GroupPublicationResolutionV2 resolution)
    {
        if (resolution == GroupPublicationResolutionV2.Unresolved)
            throw new ArgumentOutOfRangeException(nameof(resolution));
        if (Phase == GroupPublicationPhaseV2.Resolved && Resolution != resolution)
            throw new InvalidOperationException("A durable group resolution cannot change its outcome.");
        if (Phase == GroupPublicationPhaseV2.Resolved)
            return Copy();
        var digest = ProtectionDigest.GroupPublicationDecisionV2(Descriptor, BoundCommitDigest, resolution);
        return new(Descriptor, RootIdentity, GroupPublicationPhaseV2.Resolved,
            StagedStateFileIdentity, BackupStateFileIdentity, resolution, BoundCommitDigest, digest);
    }

    internal PendingGroupPublicationV2 Copy() => new(Descriptor, RootIdentity, Phase,
        StagedStateFileIdentity, BackupStateFileIdentity, Resolution, BoundCommitDigest, ResolutionDigest);

    private void RequireArtifactPair(PhysicalFileIdentity staged, PhysicalFileIdentity? backup)
    {
        if ((LocalParticipant.PriorStateFileIdentity is null) != (backup is null))
            throw new ArgumentException("The bound backup must match the local prior-state presence.", nameof(backup));
        if (LocalParticipant.PriorStateFileIdentity is { } priorIdentity &&
            (priorIdentity == staged || priorIdentity == backup))
            throw new ArgumentException("Bound artifacts must differ from the prior state identity.", nameof(staged));
        if (backup is not null && backup == staged)
            throw new ArgumentException("The stage and backup identities must be distinct.", nameof(backup));
    }
}
