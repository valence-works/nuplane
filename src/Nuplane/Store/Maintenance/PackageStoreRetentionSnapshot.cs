using System.Collections.Immutable;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Maintenance;

/// <summary>Immutable descriptive inputs captured for one root and enrollment epoch.</summary>
/// <remarks>
/// This snapshot and plans derived from it are not admission capabilities or deletion authority.
/// Protection records may contain positive facts even when their overall knowledge is Unknown.
/// </remarks>
internal sealed class PackageStoreRetentionSnapshot
{
    internal PackageStoreRetentionSnapshot(
        PhysicalRootIdentity root,
        long enrollmentEpoch,
        PackageStoreRetentionInventoryStatus inventoryStatus,
        IReadOnlyList<PackageInstallIdentity> completedInstalls,
        PackageStoreRetentionProtectionKnowledge protectionKnowledge,
        IReadOnlyList<PackageStoreRetentionProtectedInstall> protectedInstalls,
        int? keepNewestInactiveVersionsPerPackage)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(completedInstalls);
        ArgumentNullException.ThrowIfNull(protectedInstalls);
        if (enrollmentEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(enrollmentEpoch));
        if (!Enum.IsDefined(inventoryStatus))
            throw new ArgumentOutOfRangeException(nameof(inventoryStatus));
        if (!Enum.IsDefined(protectionKnowledge))
            throw new ArgumentOutOfRangeException(nameof(protectionKnowledge));
        if (keepNewestInactiveVersionsPerPackage < 0)
            throw new ArgumentOutOfRangeException(nameof(keepNewestInactiveVersionsPerPackage));

        Root = root;
        EnrollmentEpoch = enrollmentEpoch;
        InventoryStatus = inventoryStatus;
        CompletedInstalls = completedInstalls.Select(static install =>
            install ?? throw new ArgumentException("Completed installs cannot contain null entries.", nameof(completedInstalls)))
            .ToImmutableArray();
        ProtectionKnowledge = protectionKnowledge;
        ProtectedInstalls = protectedInstalls.Select(static install =>
            install ?? throw new ArgumentException("Protection entries cannot contain null entries.", nameof(protectedInstalls)))
            .ToImmutableArray();
        KeepNewestInactiveVersionsPerPackage = keepNewestInactiveVersionsPerPackage;
    }

    internal PhysicalRootIdentity Root { get; }

    internal long EnrollmentEpoch { get; }

    internal PackageStoreRetentionInventoryStatus InventoryStatus { get; }

    internal ImmutableArray<PackageInstallIdentity> CompletedInstalls { get; }

    internal PackageStoreRetentionProtectionKnowledge ProtectionKnowledge { get; }

    internal ImmutableArray<PackageStoreRetentionProtectedInstall> ProtectedInstalls { get; }

    internal int? KeepNewestInactiveVersionsPerPackage { get; }
}
