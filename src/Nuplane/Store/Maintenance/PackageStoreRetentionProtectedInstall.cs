using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Maintenance;

/// <summary>Associates an exact descriptive install identity with one positive protection fact.</summary>
internal sealed record PackageStoreRetentionProtectedInstall
{
    internal PackageStoreRetentionProtectedInstall(
        PackageInstallIdentity install,
        PackageStoreRetentionProtectionReason reason)
    {
        ArgumentNullException.ThrowIfNull(install);
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason));

        Install = install;
        Reason = reason;
    }

    internal PackageInstallIdentity Install { get; }

    internal PackageStoreRetentionProtectionReason Reason { get; }
}
