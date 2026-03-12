namespace Nuplane.Store.State;

/// <summary>
/// Describes the action taken or to be taken for a package version during cleanup.
/// </summary>
public enum CleanupAction
{
    /// <summary>The version is retained by cleanup policy.</summary>
    Kept,
    /// <summary>The version is eligible for deletion.</summary>
    Deleted,
    /// <summary>The deletion was blocked (e.g., the version is the last-known-good).</summary>
    Blocked
}