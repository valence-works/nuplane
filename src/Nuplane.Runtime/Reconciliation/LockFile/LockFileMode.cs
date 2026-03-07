namespace Nuplane.Runtime.Configuration;

/// <summary>
/// Specifies how the lock file is used during reconciliation.
/// </summary>
public enum LockFileMode
{
    /// <summary>The lock file is generated from resolved packages but not enforced.</summary>
    Generate,
    /// <summary>The lock file is enforced: resolved versions are overridden by lock entries when present.</summary>
    Enforce,
    /// <summary>The lock file is strictly enforced: every resolved package must have a lock entry.</summary>
    Strict
}