namespace Nuplane.Store.Maintenance;

/// <summary>Identifies why one exact package install must be retained.</summary>
internal enum PackageStoreRetentionProtectionReason
{
    Active = 0,
    RecoverableLastKnownGood = 1,
    LiveUse = 2
}
