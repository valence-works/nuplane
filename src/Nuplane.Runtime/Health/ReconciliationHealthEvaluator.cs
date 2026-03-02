namespace Nuplane.Runtime.Health;

public sealed class ReconciliationHealthEvaluator
{
    public bool IsDegraded { get; private set; }

    public int LastTrustFailureCount { get; private set; }

    public int LastLockFailureCount { get; private set; }

    public int LastCleanupFailureCount { get; private set; }

    public bool Evaluate(bool hadAnyFailures, bool allSourcesFresh)
    {
        if (hadAnyFailures || !allSourcesFresh)
        {
            IsDegraded = true;
            return IsDegraded;
        }

        IsDegraded = false;
        return IsDegraded;
    }

    public bool Evaluate(bool hadAnyFailures, bool allSourcesFresh, int trustFailures, int lockFailures)
    {
        LastTrustFailureCount = Math.Max(0, trustFailures);
        LastLockFailureCount = Math.Max(0, lockFailures);
        return Evaluate(hadAnyFailures || trustFailures > 0 || lockFailures > 0, allSourcesFresh);
    }

    public bool Evaluate(bool hadAnyFailures, bool allSourcesFresh, int trustFailures, int lockFailures, int cleanupFailures)
    {
        LastCleanupFailureCount = Math.Max(0, cleanupFailures);
        return Evaluate(hadAnyFailures || cleanupFailures > 0, allSourcesFresh, trustFailures, lockFailures);
    }
}
