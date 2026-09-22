using Nuplane.Abstractions;
using Nuplane.Capabilities;
using Nuplane.Feeds;
using Nuplane.Feeds.Policy;
using Nuplane.Observability;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.State;
using Nuplane.Store.Transactions;
using Nuplane.Versioning;
using NuGet.Resolver;

namespace Nuplane.Reconciliation;


/// <summary>
/// Resolves package requests and executes transactional package activation using the
/// configured resolver, transaction coordinator, retry policy, and failure recorder.
/// </summary>
/// <remarks>
/// Resolution is a fixpoint, not a single pass, when any <see cref="IDesiredStateContributor"/> is
/// registered: roots are resolved, their dependency closure is expanded, the contributors are asked
/// what the packages now on disk additionally require, each contributed request is resolved as an
/// ordinary root, and the closure is expanded again — until a round adds nothing. Everything after
/// resolution sees only the enlarged root set, so graph selection, lock-file evaluation, the trust
/// gate, the diff, and the transactions are untouched by any of this.
/// </remarks>
public sealed class PackageApplyExecutor(
    IPackageResolver packageResolver,
    PackageTransactionCoordinator transactionCoordinator,
    IReconciliationRetryPolicy retryPolicy,
    IFailureRecorder failureRecorder,
    IEnumerable<IDesiredStateContributor>? desiredStateContributors = null,
    IReconciliationLogger? reconciliationLogger = null) : IPackageApplyExecutor
{
    /// <summary>
    /// How many contribution rounds may add roots before resolution refuses instead of continuing.
    /// A contributed package may itself contribute, so the loop needs a bound; eight is far beyond
    /// any legitimate chain (the motivating case is one module requiring one engine) and small
    /// enough that an accidental cycle is refused in a bounded number of feed round-trips rather
    /// than at whatever moment something else gave up.
    /// </summary>
    internal const int MaxContributionRounds = 8;

    private readonly IPackageResolver _packageResolver = packageResolver ?? throw new ArgumentNullException(nameof(packageResolver));
    private readonly PackageTransactionCoordinator _transactionCoordinator = transactionCoordinator ?? throw new ArgumentNullException(nameof(transactionCoordinator));
    private readonly IReconciliationRetryPolicy _retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
    private readonly IFailureRecorder _failureRecorder = failureRecorder ?? throw new ArgumentNullException(nameof(failureRecorder));
    private readonly PackageDependencyGraphResolver _graphResolver = new(packageResolver, retryPolicy);
    private readonly IDesiredStateContributor[] _desiredStateContributors = desiredStateContributors?.ToArray() ?? [];
    private readonly IReconciliationLogger _reconciliationLogger = reconciliationLogger ?? new ReconciliationLogger();

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

        var resolvedRootsById = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);
        var rootRequests = new List<PackageRequest>();

        foreach (var request in desiredRequests)
        {
            try
            {
                var root = await ResolveRootAsync(request, cancellationToken);
                resolvedRootsById[request.Id] = root;
                rootRequests.Add(request);
            }
            catch (Exception ex)
            {
                await RecordRootResolutionFailureAsync(request.Id, ex);
            }
        }

        await ExpandAndContributeAsync();

        var conflictingPackageIds = resolved
            .GroupBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Select(package => package.Version).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (conflictingPackageIds.Count > 0)
        {
            var conflictFailedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var graph in graphs.Where(graph => graph.Nodes.Any(node => conflictingPackageIds.Contains(node.PackageId))))
            {
                foreach (var root in graph.Roots)
                {
                    conflictFailedPackageIds.Add(root.PackageId);
                    if (!failed.Contains(root.PackageId, StringComparer.OrdinalIgnoreCase))
                    {
                        failed.Add(root.PackageId);
                    }
                }
            }

            var conflictMessage = $"Conflicting graph package versions were resolved for package id(s): {string.Join(", ", conflictingPackageIds.Order(StringComparer.OrdinalIgnoreCase))}.";
            foreach (var packageId in conflictFailedPackageIds.Order(StringComparer.OrdinalIgnoreCase))
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

        Task<ResolvedPackage> ResolveCachedRootAsync(PackageRequest packageRequest, CancellationToken ct) =>
            resolvedRootsById.TryGetValue(packageRequest.Id, out var root)
                ? Task.FromResult(root)
                : ResolveRootAsync(packageRequest, ct);

        // Expands the closure of the current root set and, while contributors keep adding roots,
        // expands it again. Without contributors this runs the single expansion it always did.
        async Task ExpandAndContributeAsync()
        {
            var contributedSoFar = new List<PackageRequest>();
            var refusedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var recordedRefusals = new HashSet<string>(StringComparer.Ordinal);
            var roundsThatAddedRoots = 0;

            while (true)
            {
                if (!await TryExpandGraphAsync() || _desiredStateContributors.Length == 0)
                {
                    return;
                }

                var contribution = await CollectContributionsAsync(contributedSoFar);

                // Refusals are applied before anything else is resolved, so a refusal that must
                // happen before a download — a contribution that is not pinned under a pinned-only
                // restore — is honoured while nothing has been fetched for it.
                var changed = await ApplyRefusalsAsync(contribution.Refusals);
                var pending = SelectPendingRequests(contribution.Requests, refusedPackageIds);

                if (pending.Count > 0)
                {
                    if (roundsThatAddedRoots == MaxContributionRounds)
                    {
                        await RefuseContributionLimitAsync(pending, contributedSoFar);
                        return;
                    }

                    roundsThatAddedRoots++;
                    changed |= await ResolveContributedRootsAsync(pending);
                }

                // Nothing added, nothing dropped: the closure is a fixpoint and the expansion above
                // is the one every later stage sees.
                if (!changed)
                {
                    return;
                }
            }

            async Task<bool> ApplyRefusalsAsync(IReadOnlyList<ContributionRefusal> refusals)
            {
                var droppedRoot = false;

                foreach (var refusal in refusals
                    .OrderBy(static refusal => refusal.PackageId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static refusal => refusal.Stage, StringComparer.Ordinal)
                    .ThenBy(static refusal => refusal.Message, StringComparer.Ordinal))
                {
                    // Contributors are re-asked every round and legitimately repeat a refusal whose
                    // subject is still in the closure; it is recorded once.
                    if (!recordedRefusals.Add($"{refusal.PackageId}|{refusal.Stage}|{refusal.Message}"))
                    {
                        continue;
                    }

                    refusedPackageIds.Add(refusal.PackageId);
                    if (!failed.Contains(refusal.PackageId, StringComparer.OrdinalIgnoreCase))
                    {
                        failed.Add(refusal.PackageId);
                    }

                    await _failureRecorder.RecordAsync(refusal.PackageId, refusal.Stage, refusal.Message, correlationId, cancellationToken);
                    _reconciliationLogger.LogCapabilityRefused(correlationId, refusal.PackageId, refusal.Stage, refusal.Message);

                    // A refused package must not be applied, and the graph is all-or-nothing at
                    // activation time, so it is dropped from the root set and the closure is
                    // expanded again without it. Every other root — including one the operator
                    // named by hand that the refused package could not use — still applies. A
                    // refused package that another surviving root depends on stays in the closure as
                    // that root's dependency: it is not discoverable there, and its failure is
                    // recorded, which is as loud as this can be without failing the root that wants
                    // it. A root an earlier round already contributed for this package is left
                    // alone; it is an acquired root like any other, and the next cycle reaches the
                    // same decision from the same inputs.
                    droppedRoot |= rootRequests.RemoveAll(request =>
                        string.Equals(request.Id, refusal.PackageId, StringComparison.OrdinalIgnoreCase)) > 0;
                }

                return droppedRoot;
            }

            async Task<bool> ResolveContributedRootsAsync(IReadOnlyList<ContributedPackageRequest> pending)
            {
                var changedRootSet = false;

                foreach (var contributed in pending)
                {
                    var request = contributed.Request;
                    try
                    {
                        var root = await ResolveRootAsync(request, cancellationToken);
                        resolvedRootsById[request.Id] = root;
                        rootRequests.Add(request);
                        contributedSoFar.Add(request);
                        changedRootSet = true;
                    }
                    catch (Exception ex)
                    {
                        // The contributed root fails exactly as any unacquirable root does, feed
                        // decision included, so an outage or a missing version reads the same way
                        // wherever the request came from.
                        await RecordRootResolutionFailureAsync(request.Id, ex);

                        // And so does every package that asked for it: a package whose additional
                        // requirement cannot be acquired is broken, and applying it would only move
                        // the failure to load time.
                        changedRootSet |= await ApplyRefusalsAsync(contributed.DeclaringPackageIds
                            .Select(declaringPackageId => new ContributionRefusal(
                                declaringPackageId,
                                CapabilityRefusalStage.Unresolved,
                                $"Contributed root '{request.Id} {request.VersionRange}' ({request.SourceName}) could not be acquired: {ex.Message}"))
                            .ToArray());
                    }
                }

                return changedRootSet;
            }

            async Task RefuseContributionLimitAsync(
                IReadOnlyList<ContributedPackageRequest> pending,
                IReadOnlyList<PackageRequest> contributed)
            {
                var chain = string.Join(
                    " -> ",
                    contributed
                        .Concat(pending.Select(static request => request.Request))
                        .Select(static request => $"{request.Id} ({request.SourceName})"));
                var message =
                    $"Desired-state contribution did not reach a fixpoint within {MaxContributionRounds} rounds, so resolution stopped " +
                    $"and nothing further was acquired. The contribution chain is: {chain}.";

                // Both ends of the chain are refused: the packages that asked for the pending roots,
                // whose requirement is now unmet, and the pending roots themselves, which were never
                // resolved. Recording the latter is what keeps a truncated chain from looking like a
                // closure that simply had nothing more to add.
                await ApplyRefusalsAsync(pending
                    .SelectMany(request => request.DeclaringPackageIds.Append(request.Request.Id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static packageId => packageId, StringComparer.OrdinalIgnoreCase)
                    .Select(packageId => new ContributionRefusal(packageId, CapabilityRefusalStage.ContributionLimit, message))
                    .ToArray());
            }
        }

        // Replaces the previous round's expansion: the closure of a root set is a function of that
        // set, so the last expansion is the only one that describes the cycle. Roots already
        // resolved come from the cache, and an installed package is not fetched again, so a second
        // expansion costs metadata reads rather than downloads.
        async Task<bool> TryExpandGraphAsync()
        {
            resolved.Clear();
            graphs.Clear();

            if (rootRequests.Count == 0)
            {
                return true;
            }

            try
            {
                var graphResult = await _graphResolver.ResolveAsync(
                    rootRequests,
                    ResolveCachedRootAsync,
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

                return true;
            }
            catch (Exception ex)
            {
                var stage = ex switch
                {
                    FeedUnavailableException => "resolve-feed-unavailable",
                    NoEligibleFeedException => "resolve-no-eligible-feed",
                    NuGetResolverException => "resolve-graph-conflict",
                    _ => "resolve"
                };

                foreach (var request in rootRequests)
                {
                    failed.Add(request.Id);
                    await _failureRecorder.RecordAsync(request.Id, stage, ex.Message, correlationId, cancellationToken);

                    if (_packageResolver is MultiFeedPackageResolver multiFeedResolver &&
                        multiFeedResolver.TryGetDecision(request.Id, out var decision))
                    {
                        decisions.Add(decision with { CorrelationId = correlationId });
                    }
                }

                return false;
            }
        }

        // One context per round, shared by every contributor: what the host asked for, what is on
        // disk after the expansion just performed, and what earlier rounds contributed.
        async Task<DesiredStateContribution> CollectContributionsAsync(IReadOnlyList<PackageRequest> contributedSoFar)
        {
            var context = new DesiredStateContributionContext(
                correlationId,
                desiredRequests,
                resolved.ToArray(),
                contributedSoFar.ToArray());

            var requests = new List<ContributedPackageRequest>();
            var refusals = new List<ContributionRefusal>();

            foreach (var contributor in _desiredStateContributors)
            {
                // Deliberately not guarded: a contributor that throws could not compute what a
                // package requires, and a cycle that treated that as "nothing was required" would
                // report a healthy closure that is missing a root.
                var contribution = await contributor.ContributeAsync(context, cancellationToken);
                requests.AddRange(contribution.Requests);
                refusals.AddRange(contribution.Refusals);
            }

            return new(requests, refusals);
        }

        // The contributed requests this round still has to resolve: not refused, not already a root
        // (an explicit root the host named wins, and so does a root an earlier round contributed),
        // and merged when two contributions name the same package and version. Two contributions
        // that name the same package at *different* versions are deliberately left as two roots, so
        // the existing conflicting-root detection refuses them instead of one silently winning.
        List<ContributedPackageRequest> SelectPendingRequests(
            IReadOnlyList<ContributedPackageRequest> requests,
            HashSet<string> refusedPackageIds) =>
            requests
                .Where(contributed => !refusedPackageIds.Contains(contributed.Request.Id))
                .Where(contributed => !rootRequests.Any(root =>
                    string.Equals(root.Id, contributed.Request.Id, StringComparison.OrdinalIgnoreCase)))
                .GroupBy(
                    static contributed => (contributed.Request.Id, contributed.Request.VersionRange, contributed.Request.FeedName),
                    RequestIdentityComparer.Instance)
                .Select(static group => group
                    .OrderBy(static contributed => contributed.Request.SourceName, StringComparer.Ordinal)
                    .First() with
                {
                    DeclaringPackageIds = group
                        .SelectMany(static contributed => contributed.DeclaringPackageIds)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(static packageId => packageId, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                })
                .OrderBy(static contributed => contributed.Request.SourceName, StringComparer.Ordinal)
                .ThenBy(static contributed => contributed.Request.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

        async Task RecordRootResolutionFailureAsync(string packageId, Exception exception)
        {
            failed.Add(packageId);
            var stage = exception switch
            {
                FeedUnavailableException => "resolve-feed-unavailable",
                NoEligibleFeedException => "resolve-no-eligible-feed",
                _ => "resolve"
            };
            await _failureRecorder.RecordAsync(packageId, stage, exception.Message, correlationId, cancellationToken);

            if (_packageResolver is MultiFeedPackageResolver multiFeedResolver &&
                multiFeedResolver.TryGetDecision(packageId, out var decision))
            {
                decisions.Add(decision with { CorrelationId = correlationId });
            }
        }
    }

    /// <summary>
    /// Compares the identity of two contributed requests — package id and preferred feed
    /// case-insensitively, version range exactly — so that the same request contributed for two
    /// different capabilities becomes one root carrying both their declaring packages.
    /// </summary>
    private sealed class RequestIdentityComparer : IEqualityComparer<(string Id, string VersionRange, string? FeedName)>
    {
        internal static readonly RequestIdentityComparer Instance = new();

        public bool Equals((string Id, string VersionRange, string? FeedName) left, (string Id, string VersionRange, string? FeedName) right) =>
            StringComparer.OrdinalIgnoreCase.Equals(left.Id, right.Id) &&
            StringComparer.Ordinal.Equals(left.VersionRange, right.VersionRange) &&
            StringComparer.OrdinalIgnoreCase.Equals(left.FeedName, right.FeedName);

        public int GetHashCode((string Id, string VersionRange, string? FeedName) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Id),
                StringComparer.Ordinal.GetHashCode(value.VersionRange),
                value.FeedName is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(value.FeedName));
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
