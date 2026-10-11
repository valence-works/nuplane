using Nuplane.Abstractions;
using Nuplane.Observability;
using Nuplane.Reconciliation.LockFile;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.State;

namespace Nuplane.Reconciliation.Middleware;

internal sealed class TrustAndLockGateMiddleware(
    ILockFileCoordinator lockFileCoordinator,
    IReconciliationRetryPolicy retryPolicy,
    IFailureRecorder failureRecorder,
    IReconciliationLogger logger,
    ILockFileCycleCoordinator? lockFileCycleCoordinator = null) : IReconciliationMiddleware
{
    public async Task InvokeAsync(ReconciliationCycleContext context, Func<Task> next)
    {
        var resolutionResult = context.ResolutionResult!;
        var lockFailures = 0;
        var trustAndLockPassed = new List<ResolvedPackage>();
        var combinedFailures = new HashSet<string>(resolutionResult.FailedPackageIds, StringComparer.OrdinalIgnoreCase);

        foreach (var resolved in resolutionResult.ResolvedPackages)
        {
            var lockOutcome = lockFileCycleCoordinator is not null && context.LockFileSnapshot is not null
                ? lockFileCycleCoordinator.Evaluate(context.LockFileSnapshot, resolved)
                : await retryPolicy.ExecuteAsync(
                    ct => lockFileCoordinator.EvaluateAsync(resolved, ct),
                    context.CancellationToken);

            logger.LogLockOutcome(context.CorrelationId, resolved.Id, lockOutcome);

            if (!lockOutcome.Allowed || lockOutcome.EffectivePackage is null)
            {
                lockFailures++;
                combinedFailures.Add(resolved.Id);
                await failureRecorder.RecordAsync(resolved.Id, "lock", lockOutcome.ReasonCode, context.CorrelationId, context.CancellationToken);
                continue;
            }

            trustAndLockPassed.Add(lockOutcome.EffectivePackage);
        }

        context.TrustAndLockPassed = trustAndLockPassed;
        context.LockFailureCount = lockFailures;

        // A protected transition consumes exact graph/request/package joins. Keep a graph only
        // when every selected package passed the lock gate with the same resolved provenance.
        var resolvedGraphs = resolutionResult.ResolvedGraphs;
        var graphSelections = resolutionResult.GraphSelections;
        if (resolvedGraphs.Count == graphSelections.Count && resolvedGraphs.Count > 0)
        {
            var passedByKey = trustAndLockPassed.ToDictionary(
                static package => BuildKey(package.Id, package.Version),
                StringComparer.OrdinalIgnoreCase);
            var acceptedGraphs = new List<ResolvedPackageGraph>();
            var acceptedSelections = new List<ResolvedPackageGraphSelection>();
            var acceptedPackages = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);

            foreach (var selection in graphSelections)
            {
                var exactPackages = new List<ResolvedPackage>(selection.Packages.Count);
                var complete = true;
                foreach (var package in selection.Packages)
                {
                    var key = BuildKey(package.Id, package.Version);
                    if (!passedByKey.TryGetValue(key, out var passed) || !HasSameResolvedPackageContent(package, passed))
                    {
                        complete = false;
                        break;
                    }
                    exactPackages.Add(passed);
                }

                if (!complete)
                {
                    foreach (var node in selection.Graph.Nodes)
                        combinedFailures.Add(node.PackageId);
                    continue;
                }

                acceptedGraphs.Add(selection.Graph);
                acceptedSelections.Add(new(selection.Graph, selection.RootRequests, exactPackages));
                foreach (var package in exactPackages)
                    acceptedPackages.TryAdd(BuildKey(package.Id, package.Version), package);
            }

            resolvedGraphs = acceptedGraphs;
            graphSelections = acceptedSelections;
            trustAndLockPassed = acceptedPackages.Values
                .OrderBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static package => package.Version, StringComparer.OrdinalIgnoreCase)
                .ToList();
            context.TrustAndLockPassed = trustAndLockPassed;
        }

        // Update resolution result with trust/lock filtered packages and aligned exact graph selections.
        context.ResolutionResult = new(
            trustAndLockPassed,
            combinedFailures.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            resolutionResult.FeedDecisions,
            resolvedGraphs)
        {
            GraphSelections = graphSelections,
            LockFileEvaluated = resolutionResult.LockFileEvaluated,
            ExpectedArtifactHashes = resolutionResult.ExpectedArtifactHashes
        };

        await next();
    }

    private static string BuildKey(string packageId, string version) => $"{packageId}@{version}";

    private static bool HasSameResolvedPackageContent(ResolvedPackage expected, ResolvedPackage actual)
        => StringComparer.OrdinalIgnoreCase.Equals(expected.Id, actual.Id) &&
            StringComparer.OrdinalIgnoreCase.Equals(expected.Version, actual.Version) &&
            string.Equals(expected.FeedName, actual.FeedName, StringComparison.Ordinal) &&
            string.Equals(expected.InstallPath, actual.InstallPath, StringComparison.Ordinal) &&
            expected.InstalledAt == actual.InstalledAt &&
            string.Equals(expected.SourceName, actual.SourceName, StringComparison.Ordinal) &&
            string.Equals(expected.PackageContentHash, actual.PackageContentHash, StringComparison.Ordinal);
}
