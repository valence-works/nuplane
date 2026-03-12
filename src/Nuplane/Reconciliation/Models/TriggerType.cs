namespace Nuplane.Reconciliation.Models;

/// <summary>
/// Identifies the type of event that initiated a reconciliation cycle.
/// </summary>
public enum TriggerType
{
    /// <summary>A periodically scheduled reconciliation tick.</summary>
    Scheduled,

    /// <summary>A feed-monitor observation detected a change and requested reconciliation.</summary>
    ObservedChange,

    /// <summary>An explicit manual trigger from an operator or API.</summary>
    Manual,

    /// <summary>The first automatic reconciliation cycle after host startup.</summary>
    Startup
}