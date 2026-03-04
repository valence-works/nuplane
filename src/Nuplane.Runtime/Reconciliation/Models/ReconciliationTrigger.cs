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

/// <summary>
/// Describes why a reconciliation cycle was initiated, including a trigger type,
/// optional source attribution, and a correlation identifier.
/// </summary>
/// <param name="Type">The kind of trigger that initiated the cycle.</param>
/// <param name="Source">Optional attribution source (e.g., the local directory feed name for <see cref="TriggerType.DirectoryChange"/>).</param>
/// <param name="CorrelationId">The correlation identifier for the cycle; <see langword="null"/> to auto-generate.</param>
public sealed record ReconciliationTrigger(
    TriggerType Type,
    string? Source = null,
    string? CorrelationId = null);
