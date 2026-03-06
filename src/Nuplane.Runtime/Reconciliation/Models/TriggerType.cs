namespace Nuplane.Runtime.Reconciliation.Models;

/// <summary>
/// Identifies the type of event that initiated a reconciliation cycle.
/// </summary>
public enum TriggerType
{
    /// <summary>A periodically scheduled reconciliation tick.</summary>
    Scheduled,

    /// <summary>A file-system change detected by a directory watcher.</summary>
    DirectoryChange,

    /// <summary>An explicit manual trigger from an operator or API.</summary>
    Manual,

    /// <summary>The first automatic reconciliation cycle after host startup.</summary>
    Startup
}