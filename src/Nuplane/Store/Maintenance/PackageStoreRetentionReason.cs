namespace Nuplane.Store.Maintenance;

/// <summary>Explains a retention classification or an unresolved planning boundary.</summary>
internal enum PackageStoreRetentionReason
{
    RetentionPolicyAbsent = 0,
    WithinInactiveRetentionBudget = 1,
    OutsideInactiveRetentionBudget = 2,
    ProtectedActive = 3,
    ProtectedRecoverableLastKnownGood = 4,
    ProtectedLiveUse = 5,
    InventoryIncomplete = 6,
    InventoryUnknown = 7,
    ProtectionUnknown = 8,
    MalformedVersion = 9,
    MalformedInstallPath = 10,
    RootMismatch = 11,
    DuplicateIdentity = 12,
    IdentityConflict = 13,
    ProtectedInstallNotInventoried = 14,
    ProtectedIdentityRootMismatch = 15
}
