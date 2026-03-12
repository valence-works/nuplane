namespace Nuplane.Health;

/// <summary>
/// Evaluates reconciliation health by tracking lock, cleanup, manifest, source, acquisition, and admin failures.
/// </summary>
public sealed class ReconciliationHealthEvaluator : IReconciliationHealthEvaluator
{
    /// <inheritdoc />
    public bool IsDegraded { get; private set; }

    /// <inheritdoc />
    public int LastLockFailureCount { get; private set; }

    /// <inheritdoc />
    public int LastCleanupFailureCount { get; private set; }

    /// <inheritdoc />
    public int LastManifestFailureCount { get; private set; }

    /// <inheritdoc />
    public int LastSourceOutageCount { get; private set; }

    /// <inheritdoc />
    public int LastAcquisitionFailureCount { get; private set; }

    /// <inheritdoc />
    public int LastAdminRejectionCount { get; private set; }

    /// <inheritdoc />
    public bool Evaluate(ReconciliationHealthInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        LastLockFailureCount = Math.Max(0, input.LockFailures);
        LastCleanupFailureCount = Math.Max(0, input.CleanupFailures);
        LastManifestFailureCount = Math.Max(0, input.ManifestFailures);
        LastSourceOutageCount = Math.Max(0, input.SourceOutages);
        LastAcquisitionFailureCount = Math.Max(0, input.AcquisitionFailures);
        LastAdminRejectionCount = Math.Max(0, input.AdminRejections);

        var hadFailures = input.HadAnyFailures
            || input.LockFailures > 0
            || input.CleanupFailures > 0
            || input.ManifestFailures > 0
            || input.SourceOutages > 0
            || input.AcquisitionFailures > 0
            || input.AdminRejections > 0;

        IsDegraded = hadFailures || !input.AllSourcesFresh;
        return IsDegraded;
    }
}
