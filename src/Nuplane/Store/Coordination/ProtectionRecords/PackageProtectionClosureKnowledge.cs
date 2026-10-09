namespace Nuplane.Store.Coordination.ProtectionRecords;

/// <summary>Identifies whether a package-protection closure is unknown or explicitly known.</summary>
public enum PackageProtectionClosureKnowledge
{
    /// <summary>The closure is not established; it must not be treated as empty.</summary>
    Unknown = 0,

    /// <summary>The closure is established; its graph collection may be empty.</summary>
    Known = 1
}
