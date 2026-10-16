using System.Collections.Immutable;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Maintenance;

/// <summary>Immutable, descriptive retention classifications for one observed root epoch.</summary>
/// <remarks>This result is not an admission capability, deletion authorization, or execution receipt.</remarks>
internal sealed class PackageStoreRetentionPlan
{
    internal PackageStoreRetentionPlan(
        PhysicalRootIdentity root,
        long enrollmentEpoch,
        PackageStoreRetentionInventoryStatus inventoryStatus,
        PackageStoreRetentionProtectionKnowledge protectionKnowledge,
        int? keepNewestInactiveVersionsPerPackage,
        IEnumerable<PackageStoreRetentionReason> reasons,
        IEnumerable<PackageStoreRetentionPlanEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(reasons);
        ArgumentNullException.ThrowIfNull(entries);

        Root = root;
        EnrollmentEpoch = enrollmentEpoch;
        InventoryStatus = inventoryStatus;
        ProtectionKnowledge = protectionKnowledge;
        KeepNewestInactiveVersionsPerPackage = keepNewestInactiveVersionsPerPackage;
        Reasons = reasons.Distinct().Order().ToImmutableArray();
        Entries = entries.ToImmutableArray();
        HasRefusedEntries = Entries.Any(static entry => entry.Classification == PackageStoreRetentionClassification.Refused);
        HasUnresolvedEvidence = Reasons.Length > 0 || HasRefusedEntries;
    }

    internal PhysicalRootIdentity Root { get; }

    internal long EnrollmentEpoch { get; }

    internal PackageStoreRetentionInventoryStatus InventoryStatus { get; }

    internal PackageStoreRetentionProtectionKnowledge ProtectionKnowledge { get; }

    internal int? KeepNewestInactiveVersionsPerPackage { get; }

    internal ImmutableArray<PackageStoreRetentionReason> Reasons { get; }

    internal ImmutableArray<PackageStoreRetentionPlanEntry> Entries { get; }

    internal bool HasRefusedEntries { get; }

    internal bool HasUnresolvedEvidence { get; }
}
