using Nuplane.Abstractions;
using Nuplane.Feeds;
using Nuplane.Feeds.Policy;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.State;
using Nuplane.Store.Transactions;
using Nuplane.Versioning;

namespace Nuplane.Reconciliation;


/// <summary>
/// Resolves package requests and executes transactional package activation using the
/// configured resolver, transaction coordinator, retry policy, and failure recorder.
/// </summary>
public sealed class PackageApplyExecutor(
    IPackageResolver packageResolver,
    PackageTransactionCoordinator transactionCoordinator,
    IReconciliationRetryPolicy retryPolicy,
    IFailureRecorder failureRecorder) : IPackageApplyExecutor
{
    private readonly IPackageResolver _packageResolver = packageResolver ?? throw new ArgumentNullException(nameof(packageResolver));
    private readonly PackageTransactionCoordinator _transactionCoordinator = transactionCoordinator ?? throw new ArgumentNullException(nameof(transactionCoordinator));
    private readonly IReconciliationRetryPolicy _retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
    private readonly IFailureRecorder _failureRecorder = failureRecorder ?? throw new ArgumentNullException(nameof(failureRecorder));
    private readonly PackageDependencyGraphResolver _graphResolver = new(packageResolver, retryPolicy);

    /// <inheritdoc />
    public async Task<PackageResolutionResult> ResolveAsync(
        IReadOnlyList<PackageRequest> desiredRequests,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desiredRequests);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var resolved = new List<ResolvedPackage>();
        var failed = new List<string>();
        var decisions = new List<FeedResolutionDecision>();
        var graphs = new List<ResolvedPackageGraph>();

        foreach (var request in desiredRequests)
        {
            try
            {
                var graphResult = await _graphResolver.ResolveAsync(
                    [request],
                    ResolveRootAsync,
                    cancellationToken);
                resolved.AddRange(graphResult.ResolvedPackages);
                graphs.AddRange(graphResult.ResolvedGraphs);

                if (_packageResolver is MultiFeedPackageResolver multiFeedResolver)
                {
                    foreach (var package in graphResult.ResolvedPackages)
                    {
                        if (multiFeedResolver.TryGetDecision(package.Id, out var decision))
                        {
                            decisions.Add(decision with { CorrelationId = correlationId });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                failed.Add(request.Id);
                var stage = ex switch
                {
                    FeedUnavailableException => "resolve-feed-unavailable",
                    NoEligibleFeedException => "resolve-no-eligible-feed",
                    _ => "resolve"
                };
                await _failureRecorder.RecordAsync(request.Id, stage, ex.Message, correlationId, cancellationToken);

                if (_packageResolver is MultiFeedPackageResolver multiFeedResolver &&
                    multiFeedResolver.TryGetDecision(request.Id, out var decision))
                {
                    decisions.Add(decision with { CorrelationId = correlationId });
                }
            }
        }

        var conflictingPackageIds = resolved
            .GroupBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Select(package => package.Version).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (conflictingPackageIds.Count > 0)
        {
            foreach (var graph in graphs.Where(graph => graph.Nodes.Any(node => conflictingPackageIds.Contains(node.PackageId))))
            {
                foreach (var root in graph.Roots)
                {
                    if (!failed.Contains(root.PackageId, StringComparer.OrdinalIgnoreCase))
                    {
                        failed.Add(root.PackageId);
                    }
                }
            }

            var conflictMessage = $"Conflicting graph package versions were resolved for package id(s): {string.Join(", ", conflictingPackageIds.Order(StringComparer.OrdinalIgnoreCase))}.";
            foreach (var packageId in failed.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await _failureRecorder.RecordAsync(packageId, "resolve-graph-conflict", conflictMessage, correlationId, cancellationToken);
            }

            graphs = graphs
                .Where(graph => graph.Nodes.All(node => !conflictingPackageIds.Contains(node.PackageId)))
                .ToList();
            var validPackageKeys = graphs
                .SelectMany(static graph => graph.Nodes)
                .Select(static node => BuildKey(node.PackageId, node.Version))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            resolved = resolved
                .Where(package => validPackageKeys.Contains(BuildKey(package.Id, package.Version)))
                .ToList();
        }

        var deduplicatedResolved = resolved
            .GroupBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(static package => VersionKey.Create(package.Version))
                .ThenBy(static package => package.SourceName, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var deduplicatedDecisions = decisions
            .GroupBy(static decision => decision.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.Last())
            .OrderBy(static decision => decision.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new(deduplicatedResolved, failed, deduplicatedDecisions, graphs);

        Task<ResolvedPackage> ResolveRootAsync(PackageRequest packageRequest, CancellationToken ct) =>
            _retryPolicy.ExecuteAsync(
                token => _packageResolver.ResolveAsync(packageRequest, token),
                ct);
    }

    /// <inheritdoc />
    public async Task<PackageApplyExecutionResult> ExecuteTransactionsAsync(
        PackageResolutionResult resolutionResult,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolutionResult);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var applied = new List<ResolvedPackage>();
        var failed = new List<string>(resolutionResult.FailedPackageIds);
        var failureMessages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (resolutionResult.ResolvedGraphs.Count == 0)
        {
            foreach (var resolved in resolutionResult.ResolvedPackages)
            {
                var transaction = await _transactionCoordinator.ExecuteAsync(
                    new(resolved.Id, resolved.Version, correlationId),
                    cancellationToken);

                if (transaction.Succeeded)
                {
                    applied.Add(resolved);
                }
                else
                {
                    failed.Add(resolved.Id);
                    if (!string.IsNullOrWhiteSpace(transaction.FailureMessage))
                    {
                        failureMessages[resolved.Id] = transaction.FailureMessage;
                    }
                }
            }

            return new(applied, failed, failureMessages);
        }

        var packagesByKey = resolutionResult.ResolvedPackages
            .ToDictionary(package => BuildKey(package.Id, package.Version), package => package, StringComparer.OrdinalIgnoreCase);
        var appliedByKey = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);

        foreach (var graph in resolutionResult.ResolvedGraphs)
        {
            var graphApplied = new List<ResolvedPackage>();
            var graphFailures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in graph.Nodes.OrderBy(static node => node.PackageId, StringComparer.OrdinalIgnoreCase))
            {
                var key = BuildKey(node.PackageId, node.Version);
                if (appliedByKey.TryGetValue(key, out var alreadyApplied))
                {
                    graphApplied.Add(alreadyApplied);
                    continue;
                }

                if (!packagesByKey.TryGetValue(key, out var resolved))
                {
                    graphFailures[node.PackageId] = $"Resolved package '{node.PackageId}@{node.Version}' was missing from graph resolution output.";
                    continue;
                }

                var transaction = await _transactionCoordinator.ExecuteAsync(
                    new(resolved.Id, resolved.Version, correlationId),
                    cancellationToken);

                if (transaction.Succeeded)
                {
                    graphApplied.Add(resolved);
                }
                else
                {
                    graphFailures[resolved.Id] = string.IsNullOrWhiteSpace(transaction.FailureMessage)
                        ? $"Package '{resolved.Id}' failed to apply."
                        : transaction.FailureMessage;
                }
            }

            if (graphFailures.Count == 0)
            {
                foreach (var graphPackage in graphApplied)
                {
                    appliedByKey[BuildKey(graphPackage.Id, graphPackage.Version)] = graphPackage;
                }

                continue;
            }

            var graphFailureMessage = $"Graph '{graph.GraphId}' activation failed; no graph nodes were published.";
            foreach (var node in graph.Nodes)
            {
                if (!failed.Contains(node.PackageId, StringComparer.OrdinalIgnoreCase))
                {
                    failed.Add(node.PackageId);
                }

                failureMessages[node.PackageId] = graphFailures.TryGetValue(node.PackageId, out var nodeFailure)
                    ? nodeFailure
                    : graphFailureMessage;
            }
        }

        return new(
            appliedByKey.Values
                .OrderBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static package => package.Version, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            failed.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            failureMessages);
    }

    /// <inheritdoc />
    public async Task RecordLoadingFailureNonMutatingAsync(
        string packageId,
        string correlationId,
        string message,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        await _failureRecorder.RecordAsync(packageId, "load", message, correlationId, cancellationToken);
    }

    private static string BuildKey(string packageId, string version) => $"{packageId}@{version}";
}
