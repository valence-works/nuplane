namespace Nuplane.Abstractions;

/// <summary>
/// Health state of the convergence system.
/// </summary>
public enum ConvergenceHealthState
{
    /// <summary>System is healthy with no outstanding failures.</summary>
    Healthy,

    /// <summary>System has degraded behavior due to failures.</summary>
    Degraded
}