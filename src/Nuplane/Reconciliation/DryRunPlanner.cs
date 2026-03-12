using Nuplane.Abstractions;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Reconciliation;


/// <summary>
/// Builds a dry-run plan by computing the change set without mutating any state.
/// </summary>
public sealed class DryRunPlanner(IDesiredActualDiffEngine diffEngine) : IDryRunPlanner
{
    private readonly IDesiredActualDiffEngine _diffEngine = diffEngine ?? throw new ArgumentNullException(nameof(diffEngine));

    /// <inheritdoc />
    public Task<DryRunPlan> BuildPlanAsync(
        IReadOnlyCollection<ResolvedPackage> desired,
        IReadOnlyDictionary<string, string> activeVersions,
        string correlationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var changeSet = _diffEngine.Compute(desired, activeVersions, correlationId, DateTimeOffset.UtcNow);
        return Task.FromResult(new DryRunPlan(changeSet, MutatedState: false));
    }
}
