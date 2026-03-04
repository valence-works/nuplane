namespace Nuplane.Runtime.Health;

/// <summary>
/// Evaluates reconciliation health by tracking trust, lock, cleanup, and unload failures.
/// </summary>
public sealed class ReconciliationHealthEvaluator : IReconciliationHealthEvaluator
{
    /// <inheritdoc />
    public bool IsDegraded { get; private set; }

    /// <inheritdoc />
    public int LastTrustFailureCount { get; private set; }

    /// <inheritdoc />
    public int LastLockFailureCount { get; private set; }

    /// <inheritdoc />
    public int LastCleanupFailureCount { get; private set; }

    /// <inheritdoc />
    public int LastUnloadPendingCount { get; private set; }

    /// <inheritdoc />
    public int LastManifestFailureCount { get; private set; }

    /// <inheritdoc />
    public int LastSourceOutageCount { get; private set; }

    /// <inheritdoc />
    public int LastAcquisitionFailureCount { get; private set; }

    /// <inheritdoc />
    public int LastLoaderFailureCount { get; private set; }

    /// <inheritdoc />
    public int LastAdminRejectionCount { get; private set; }

    /// <inheritdoc />
    public bool Evaluate(ReconciliationHealthInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        LastTrustFailureCount = Math.Max(0, input.TrustFailures);
        LastLockFailureCount = Math.Max(0, input.LockFailures);
        LastCleanupFailureCount = Math.Max(0, input.CleanupFailures);
        LastUnloadPendingCount = Math.Max(0, input.UnloadPendingCount);
        LastManifestFailureCount = Math.Max(0, input.ManifestFailures);
        LastSourceOutageCount = Math.Max(0, input.SourceOutages);
        LastAcquisitionFailureCount = Math.Max(0, input.AcquisitionFailures);
        LastLoaderFailureCount = Math.Max(0, input.LoaderFailures);
        LastAdminRejectionCount = Math.Max(0, input.AdminRejections);

        var hadFailures = input.HadAnyFailures
            || input.TrustFailures > 0
            || input.LockFailures > 0
            || input.CleanupFailures > 0
            || input.UnloadPendingCount > 0
            || input.ManifestFailures > 0
            || input.SourceOutages > 0
            || input.AcquisitionFailures > 0
            || input.LoaderFailures > 0
            || input.AdminRejections > 0;

        IsDegraded = hadFailures || !input.AllSourcesFresh;
        return IsDegraded;
    }
}
