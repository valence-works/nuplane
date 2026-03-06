using Nuplane.Abstractions;
using Nuplane.Store.State;
using Nuplane.Store.Transactions;
using Nuplane.Runtime.Reconciliation.Models;
using Nuplane.Runtime.Reconciliation.FeedPolicy;

namespace Nuplane.Runtime.Reconciliation;


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

        foreach (var request in desiredRequests)
        {
            try
            {
                var pkg = await _retryPolicy.ExecuteAsync(
                    ct => _packageResolver.ResolveAsync(request, ct),
                    cancellationToken);
                resolved.Add(pkg);

                if (_packageResolver is MultiFeedPackageResolver multiFeedResolver &&
                    multiFeedResolver.TryGetDecision(request.Id, out var decision))
                {
                    decisions.Add(decision with { CorrelationId = correlationId });
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

        return new(resolved, failed, decisions);
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
}
