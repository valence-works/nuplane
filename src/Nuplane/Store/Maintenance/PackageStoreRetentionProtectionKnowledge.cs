namespace Nuplane.Store.Maintenance;

/// <summary>Distinguishes a complete protection snapshot from unavailable protection evidence.</summary>
internal enum PackageStoreRetentionProtectionKnowledge
{
    Known = 0,
    Unknown = 1
}
