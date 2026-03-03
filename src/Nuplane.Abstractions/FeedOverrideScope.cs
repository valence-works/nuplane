namespace Nuplane.Abstractions;

/// <summary>
/// Specifies the scope at which a feed trust override applies.
/// </summary>
public enum FeedOverrideScope
{
    /// <summary>No override is active.</summary>
    None,
    /// <summary>Override applies to a specific package.</summary>
    Package,
    /// <summary>Override applies via a feed rule configuration.</summary>
    FeedRule
}