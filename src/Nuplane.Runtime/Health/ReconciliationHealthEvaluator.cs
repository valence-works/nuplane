namespace Nuplane.Runtime.Health;

public sealed class ReconciliationHealthEvaluator
{
    public bool IsDegraded { get; private set; }

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
}
