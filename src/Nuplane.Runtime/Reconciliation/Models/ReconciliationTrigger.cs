namespace Nuplane.Runtime.Reconciliation.Models;

/// <summary>
/// Describes why a reconciliation cycle was initiated, including a trigger type,
/// optional source attribution, optional observation-kind metadata, and a correlation identifier.
/// </summary>
/// <param name="Type">The kind of trigger that initiated the cycle.</param>
/// <param name="Source">Optional attribution source (for example, the feed name associated with <see cref="TriggerType.ObservedChange"/>).</param>
/// <param name="CorrelationId">The correlation identifier for the cycle; <see langword="null"/> to auto-generate.</param>
/// <param name="ObservationKind">Optional observation mechanism metadata when <see cref="Type"/> is <see cref="TriggerType.ObservedChange"/>.</param>
public sealed record ReconciliationTrigger(
    TriggerType Type,
    string? Source = null,
    string? CorrelationId = null,
    FeedObservationKind? ObservationKind = null);
