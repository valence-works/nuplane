namespace Nuplane.Runtime.Operational;

/// <summary>
/// Represents the health state of the Nuplane runtime.
/// </summary>
public enum HealthState
{
    /// <summary>All systems operating normally.</summary>
    Healthy,

    /// <summary>One or more subsystems are degraded.</summary>
    Degraded
}