namespace Nuplane.Health;

/// <summary>
/// Input data for reconciliation health evaluation, capturing failure and staleness signals.
/// </summary>
/// <param name="HadAnyFailures">Whether any package failures occurred during the cycle.</param>
/// <param name="AllSourcesFresh">Whether all desired-state sources were read successfully.</param>
/// <param name="LockFailures">The number of packages rejected by lock file evaluation.</param>
/// <param name="CleanupFailures">The number of cleanup operations that were blocked.</param>
/// <param name="ManifestFailures">The number of manifest read failures during the cycle.</param>
/// <param name="SourceOutages">The number of source outage events during the cycle.</param>
/// <param name="AcquisitionFailures">The number of package acquisition failures during the cycle.</param>
/// <param name="AdminRejections">The number of admin trigger rejections during the cycle.</param>
public sealed record ReconciliationHealthInput(
    bool HadAnyFailures,
    bool AllSourcesFresh,
    int LockFailures,
    int CleanupFailures,
    int ManifestFailures = 0,
    int SourceOutages = 0,
    int AcquisitionFailures = 0,
    int AdminRejections = 0);

