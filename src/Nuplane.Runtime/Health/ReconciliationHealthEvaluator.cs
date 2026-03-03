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
    public bool Evaluate(ReconciliationHealthInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        LastTrustFailureCount = Math.Max(0, input.TrustFailures);
        LastLockFailureCount = Math.Max(0, input.LockFailures);
        LastCleanupFailureCount = Math.Max(0, input.CleanupFailures);
        LastUnloadPendingCount = Math.Max(0, input.UnloadPendingCount);

        var hadFailures = input.HadAnyFailures
            || input.TrustFailures > 0
            || input.LockFailures > 0
            || input.CleanupFailures > 0
            || input.UnloadPendingCount > 0;

        IsDegraded = hadFailures || !input.AllSourcesFresh;
        return IsDegraded;
    }
}
