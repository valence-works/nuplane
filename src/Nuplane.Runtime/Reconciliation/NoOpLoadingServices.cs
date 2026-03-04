using Nuplane.Abstractions;
using Nuplane.Loading;

namespace Nuplane.Runtime.Reconciliation;

internal sealed class NoOpPackageLoader : IPackageLoader
{
    public Task<PackageLoadResult> EnsureLoadedAsync(
        IReadOnlyList<ResolvedPackage> packages,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(sharedPolicy);

        var failed = packages
            .Select(x => x.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                id => id,
                _ => "Loading services are not registered. Call AddNuplaneLoading() from Nuplane.Loading.",
                StringComparer.OrdinalIgnoreCase);

        return Task.FromResult(new PackageLoadResult([], failed));
    }

    public bool TryRemoveContext(string packageId, string version, out PackageLoadContextHandle? context)
    {
        context = null;
        return false;
    }

    public bool TryGetContext(string packageId, string version, out PackageLoadContextHandle? context)
    {
        context = null;
        return false;
    }
}

internal sealed class NoOpPackageUnloadCoordinator : IPackageUnloadCoordinator
{
    public Task<(DeactivationAttempt deactivation, UnloadOutcomeRecord unload)> AttemptUnloadAsync(
        string packageId,
        PackageLoadContextHandle context,
        TimeSpan deactivationTimeout,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var deactivation = new DeactivationAttempt(
            packageId,
            now,
            (int)Math.Max(0, deactivationTimeout.TotalMilliseconds),
            Completed: false,
            TimedOut: false,
            OutcomeCode: "loading-services-not-registered",
            correlationId);

        var unload = new UnloadOutcomeRecord(
            packageId,
            AttemptNumber: 1,
            AttemptedAt: now,
            Outcome: UnloadOutcome.Failed,
            PendingReason: "Loading services are not registered.",
            RetryEligible: true,
            correlationId);

        return Task.FromResult((deactivation, unload));
    }
}
