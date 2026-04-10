namespace Nuplane.Health;

/// <summary>
/// Evaluates reconciliation health by tracking lock, cleanup, manifest, source, acquisition, and admin failures.
/// </summary>
public sealed class ReconciliationHealthEvaluator : IReconciliationHealthEvaluator
{
    private bool _lastHadFailures;
    private bool _lastAllSourcesFresh = true;
    private IReadOnlyList<Nuplane.Operational.OperationalStateContribution> _lastOperationalStateContributions = [];

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
    public IReadOnlyList<Nuplane.Operational.OperationalStateContribution> LastOperationalStateContributions => _lastOperationalStateContributions;

    /// <inheritdoc />
    public bool Evaluate(ReconciliationHealthInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        _lastHadFailures = input.HadAnyFailures;
        _lastAllSourcesFresh = input.AllSourcesFresh;
        LastLockFailureCount = Math.Max(0, input.LockFailures);
        LastCleanupFailureCount = Math.Max(0, input.CleanupFailures);
        LastManifestFailureCount = Math.Max(0, input.ManifestFailures);
        LastSourceOutageCount = Math.Max(0, input.SourceOutages);
        LastAcquisitionFailureCount = Math.Max(0, input.AcquisitionFailures);
        LastAdminRejectionCount = Math.Max(0, input.AdminRejections);
        _lastOperationalStateContributions = (input.OperationalStateContributions ?? []).ToArray();

        Recompute();
        return IsDegraded;
    }

    /// <summary>
    /// Updates the current module-owned operational-state contributions without overwriting core failure counts.
    /// </summary>
    public void UpdateOperationalStateContributions(IReadOnlyList<Nuplane.Operational.OperationalStateContribution> contributions)
    {
        ArgumentNullException.ThrowIfNull(contributions);

        _lastOperationalStateContributions = contributions.ToArray();
        Recompute();
    }

    private void Recompute()
    {
        var hadFailures = _lastHadFailures
            || LastLockFailureCount > 0
            || LastCleanupFailureCount > 0
            || LastManifestFailureCount > 0
            || LastSourceOutageCount > 0
            || LastAcquisitionFailureCount > 0
            || LastAdminRejectionCount > 0
            || _lastOperationalStateContributions.Any(static contribution => contribution.IsDegraded);

        IsDegraded = hadFailures || !_lastAllSourcesFresh;
    }
}
