namespace Nuplane.Reconciliation.Configuration;

/// <summary>
/// Controls how startup reconciliation failures affect host startup.
/// </summary>
public enum StartupFailurePolicy
{
    /// <summary>
    /// Throw a startup exception and prevent host startup when startup reconciliation is degraded.
    /// </summary>
    FailHost = 0,

    /// <summary>
    /// Allow host startup to continue while preserving degraded reconciliation state.
    /// </summary>
    StartDegraded = 1,

    /// <summary>
    /// Reserved for a future policy that starts from validated last-known-good state.
    /// </summary>
    UseLastKnownGood = 2
}
