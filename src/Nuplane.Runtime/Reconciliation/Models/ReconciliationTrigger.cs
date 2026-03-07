namespace Nuplane.Runtime.Reconciliation.Models;

/// <summary>
/// Describes why a reconciliation cycle was initiated, including a trigger type,
/// correlation identifier, and optional structured origin for observed-change triggers.
/// </summary>
public sealed record ReconciliationTrigger
{
    /// <summary>
    /// Initializes a new trigger for startup, scheduled, or manual reconciliation.
    /// </summary>
    public ReconciliationTrigger(TriggerType type, string? correlationId = null)
    {
        if (type == TriggerType.ObservedChange)
        {
            throw new ArgumentException("Observed change triggers require a structured observed origin.", nameof(type));
        }

        Type = type;
        CorrelationId = correlationId;
    }

    /// <summary>
    /// Initializes a new observed-change trigger.
    /// </summary>
    public ReconciliationTrigger(FeedObservationOrigin observedOrigin, string? correlationId = null)
    {
        ObservedOrigin = observedOrigin ?? throw new ArgumentNullException(nameof(observedOrigin));
        Type = TriggerType.ObservedChange;
        CorrelationId = correlationId;
    }

    /// <summary>
    /// Compatibility constructor for existing call sites that still pass observed-change metadata separately.
    /// </summary>
    public ReconciliationTrigger(
        TriggerType type,
        string? source,
        string? correlationId,
        FeedObservationKind? observationKind)
    {
        if (type == TriggerType.ObservedChange)
        {
            if (observationKind is null)
            {
                throw new ArgumentException("Observed change triggers require an observation kind.", nameof(observationKind));
            }

            ObservedOrigin = new(source ?? throw new ArgumentNullException(nameof(source)), observationKind.Value);
        }
        else if (!string.IsNullOrWhiteSpace(source) || observationKind is not null)
        {
            throw new ArgumentException("Only observed change triggers may specify observed-origin metadata.", nameof(source));
        }

        Type = type;
        CorrelationId = correlationId;
    }

    /// <summary>Gets the trigger type.</summary>
    public TriggerType Type { get; }

    /// <summary>Gets the correlation identifier for the cycle; <see langword="null"/> to auto-generate.</summary>
    public string? CorrelationId { get; }

    /// <summary>Gets the structured origin for observed-change triggers.</summary>
    public FeedObservationOrigin? ObservedOrigin { get; }

    /// <summary>Gets the observed feed name for compatibility with existing source-oriented logging.</summary>
    public string? Source => ObservedOrigin?.FeedName;

    /// <summary>Gets the observation mechanism for compatibility with existing call sites.</summary>
    public FeedObservationKind? ObservationKind => ObservedOrigin?.Kind;

    /// <summary>Creates a startup trigger.</summary>
    public static ReconciliationTrigger Startup(string? correlationId = null) =>
        new(TriggerType.Startup, correlationId);

    /// <summary>Creates a scheduled trigger.</summary>
    public static ReconciliationTrigger Scheduled(string? correlationId = null) =>
        new(TriggerType.Scheduled, correlationId);

    /// <summary>Creates a manual trigger.</summary>
    public static ReconciliationTrigger Manual(string? correlationId = null) =>
        new(TriggerType.Manual, correlationId);

    /// <summary>Creates an observed-change trigger.</summary>
    public static ReconciliationTrigger Observed(FeedObservationOrigin observedOrigin, string? correlationId = null) =>
        new(observedOrigin, correlationId);
}
