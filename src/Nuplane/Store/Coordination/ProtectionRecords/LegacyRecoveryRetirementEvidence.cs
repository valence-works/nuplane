using System.Collections.ObjectModel;
using Nuplane.Store.Coordination;

namespace Nuplane.Store.Coordination.ProtectionRecords;

/// <summary>Records source-bound operator retirement of legacy LKG entries absent from the verified active state.</summary>
/// <remarks>This is durable audit evidence, not a graph-selection or package-deletion capability.</remarks>
internal sealed class LegacyRecoveryRetirementEvidence
{
    internal LegacyRecoveryRetirementEvidence(
        string memberId,
        string sourceStateSlotDigest,
        string sourceStateFileIdentityDigest,
        string sourceStateBodyDigest,
        string activeClosureDigest,
        IEnumerable<KeyValuePair<string, string>> retiredLastKnownGood,
        long retiringEpoch,
        long retiringRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberId);
        ProtectionDigest.ValidateCanonicalDigest(sourceStateSlotDigest);
        ProtectionDigest.ValidateCanonicalDigest(sourceStateFileIdentityDigest);
        ProtectionDigest.ValidateCanonicalDigest(sourceStateBodyDigest);
        ProtectionDigest.ValidateCanonicalDigest(activeClosureDigest);
        ArgumentNullException.ThrowIfNull(retiredLastKnownGood);
        if (retiringEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(retiringEpoch));
        if (retiringRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(retiringRevision));

        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in retiredLastKnownGood)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || !NuGet.Versioning.NuGetVersion.TryParse(entry.Value, out _) ||
                !entries.TryAdd(entry.Key, entry.Value))
                throw new ArgumentException("Retired legacy recovery entries must have unique package IDs and valid versions.", nameof(retiredLastKnownGood));
        }
        if (entries.Count == 0)
            throw new ArgumentException("Legacy recovery retirement must identify at least one retired LKG entry.", nameof(retiredLastKnownGood));

        MemberId = memberId;
        SourceStateSlotDigest = sourceStateSlotDigest;
        SourceStateFileIdentityDigest = sourceStateFileIdentityDigest;
        SourceStateBodyDigest = sourceStateBodyDigest;
        ActiveClosureDigest = activeClosureDigest;
        RetiredLastKnownGood = new ReadOnlyDictionary<string, string>(entries);
        RetiringEpoch = retiringEpoch;
        RetiringRevision = retiringRevision;
    }

    internal string MemberId { get; }
    internal string SourceStateSlotDigest { get; }
    internal string SourceStateFileIdentityDigest { get; }
    internal string SourceStateBodyDigest { get; }
    internal string ActiveClosureDigest { get; }
    internal IReadOnlyDictionary<string, string> RetiredLastKnownGood { get; }
    internal long RetiringEpoch { get; }
    internal long RetiringRevision { get; }

    internal LegacyRecoveryRetirementEvidence Copy()
        => new(MemberId, SourceStateSlotDigest, SourceStateFileIdentityDigest, SourceStateBodyDigest,
            ActiveClosureDigest, RetiredLastKnownGood, RetiringEpoch, RetiringRevision);

    internal bool HasSamePayloadAs(LegacyRecoveryRetirementEvidence other)
        => string.Equals(MemberId, other.MemberId, StringComparison.Ordinal) &&
           string.Equals(SourceStateSlotDigest, other.SourceStateSlotDigest, StringComparison.Ordinal) &&
           string.Equals(SourceStateFileIdentityDigest, other.SourceStateFileIdentityDigest, StringComparison.Ordinal) &&
           string.Equals(SourceStateBodyDigest, other.SourceStateBodyDigest, StringComparison.Ordinal) &&
           string.Equals(ActiveClosureDigest, other.ActiveClosureDigest, StringComparison.Ordinal) &&
           RetiringEpoch == other.RetiringEpoch && RetiringRevision == other.RetiringRevision &&
           RetiredLastKnownGood.Count == other.RetiredLastKnownGood.Count &&
           RetiredLastKnownGood.All(pair => other.RetiredLastKnownGood.TryGetValue(pair.Key, out var version) &&
               string.Equals(pair.Value, version, StringComparison.OrdinalIgnoreCase));
}
