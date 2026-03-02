namespace Nuplane.Store.State;

public sealed class PackageCleanupService
{
    private readonly CleanupPolicyEvaluator evaluator;

    public PackageCleanupService(CleanupPolicyEvaluator evaluator)
    {
        this.evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    }

    public Task<IReadOnlyList<CleanupDecision>> ExecuteAutomaticAsync(
        IReadOnlyList<PackageVersionEntry> packageVersions,
        CleanupPolicyOptions options,
        string correlationId,
        bool triggerOnSuccessfulReconciliation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageVersions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        cancellationToken.ThrowIfCancellationRequested();

        if (options.Mode == CleanupExecutionMode.ManualOnly || !triggerOnSuccessfulReconciliation)
        {
            var kept = packageVersions
                .Select(x => new CleanupDecision(x.PackageId, x.Version, CleanupAction.Kept, "manual-only-or-not-triggered", DateTimeOffset.UtcNow, correlationId))
                .ToArray();
            return Task.FromResult<IReadOnlyList<CleanupDecision>>(kept);
        }

        var now = DateTimeOffset.UtcNow;
        var results = new List<CleanupDecision>(packageVersions.Count);

        foreach (var byPackage in packageVersions.GroupBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = byPackage
                .OrderByDescending(x => x.CapturedAt)
                .ThenByDescending(x => x.Version, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            for (var index = 0; index < ordered.Length; index++)
            {
                var item = ordered[index];
                var decision = evaluator.Evaluate(
                    item.PackageId,
                    item.Version,
                    item.CapturedAt,
                    versionOrdinalFromNewest: index + 1,
                    isLastKnownGood: item.IsLastKnownGood,
                    options,
                    now,
                    correlationId);

                results.Add(decision);
            }
        }

        return Task.FromResult<IReadOnlyList<CleanupDecision>>(results);
    }
}
