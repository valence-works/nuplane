namespace Nuplane.Abstractions;

/// <summary>
/// Type of reconciliation cycle trigger.
/// </summary>
public enum ReconciliationTriggerType
{
    /// <summary>Triggered at application startup.</summary>
    Startup,

    /// <summary>Triggered by periodic polling.</summary>
    Polling,

    /// <summary>Triggered by an explicit manual request.</summary>
    Manual
}