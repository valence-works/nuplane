using System.Collections.Immutable;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Maintenance;

/// <summary>Describes one completed install's retention classification and reasons.</summary>
internal sealed class PackageStoreRetentionPlanEntry
{
    internal PackageStoreRetentionPlanEntry(
        PackageInstallIdentity install,
        PackageStoreRetentionClassification classification,
        IEnumerable<PackageStoreRetentionReason> reasons)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(reasons);
        if (!Enum.IsDefined(classification))
            throw new ArgumentOutOfRangeException(nameof(classification));

        Install = install;
        Classification = classification;
        Reasons = reasons.Distinct().Order().ToImmutableArray();
    }

    internal PackageInstallIdentity Install { get; }

    internal PackageStoreRetentionClassification Classification { get; }

    internal ImmutableArray<PackageStoreRetentionReason> Reasons { get; }
}
