using Nuplane.Abstractions;

namespace Nuplane.Runtime.Reconciliation;

public sealed record DryRunPlan(PackageChangeSet ChangeSet, bool MutatedState);

public sealed class DryRunPlanner(DesiredActualDiffEngine diffEngine)
{
    private readonly DesiredActualDiffEngine diffEngine = diffEngine ?? throw new ArgumentNullException(nameof(diffEngine));

    public Task<DryRunPlan> BuildPlanAsync(
        IReadOnlyCollection<ResolvedPackage> desired,
        IReadOnlyDictionary<string, string> activeVersions,
        string correlationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var changeSet = diffEngine.Compute(desired, activeVersions, correlationId, DateTimeOffset.UtcNow);
        return Task.FromResult(new DryRunPlan(changeSet, MutatedState: false));
    }
}
