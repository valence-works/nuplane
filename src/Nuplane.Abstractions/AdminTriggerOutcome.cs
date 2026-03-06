namespace Nuplane.Abstractions;

/// <summary>
/// Outcome code for an admin trigger operation.
/// </summary>
public enum AdminTriggerOutcome
{
    /// <summary>Trigger was accepted and queued for execution.</summary>
    Accepted,

    /// <summary>Trigger was rejected (e.g., single-flight protection).</summary>
    Rejected,

    /// <summary>Admin surface is unavailable.</summary>
    Unavailable,

    /// <summary>Trigger completed execution.</summary>
    Completed
}