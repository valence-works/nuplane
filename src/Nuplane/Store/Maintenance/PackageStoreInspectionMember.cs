namespace Nuplane.Store.Maintenance;

/// <summary>Detached revision and digest evidence for one enrolled state member.</summary>
internal sealed record PackageStoreInspectionMember
{
    internal PackageStoreInspectionMember(
        string memberId,
        long revision,
        string stateBodyDigest,
        string protectionDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberId);
        if (revision <= 0)
            throw new ArgumentOutOfRangeException(nameof(revision));
        ArgumentException.ThrowIfNullOrWhiteSpace(stateBodyDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(protectionDigest);

        MemberId = memberId;
        Revision = revision;
        StateBodyDigest = stateBodyDigest;
        ProtectionDigest = protectionDigest;
    }

    internal string MemberId { get; }

    internal long Revision { get; }

    internal string StateBodyDigest { get; }

    internal string ProtectionDigest { get; }

    internal PackageStoreInspectionMember Copy()
        => new(MemberId, Revision, StateBodyDigest, ProtectionDigest);
}
