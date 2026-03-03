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
    private readonly IPackageResolver packageResolver = packageResolver ?? throw new ArgumentNullException(nameof(packageResolver));
    private readonly PackageTransactionCoordinator transactionCoordinator = transactionCoordinator ?? throw new ArgumentNullException(nameof(transactionCoordinator));
    private readonly IReconciliationRetryPolicy retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
    private readonly IFailureRecorder failureRecorder = failureRecorder ?? throw new ArgumentNullException(nameof(failureRecorder));

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
                var pkg = await retryPolicy.ExecuteAsync(
                    ct => packageResolver.ResolveAsync(request, ct),
                    cancellationToken);
                resolved.Add(pkg);

                if (packageResolver is MultiFeedPackageResolver multiFeedResolver &&
                    multiFeedResolver.TryGetDecision(request.Id, out var decision))
                {
                    decisions.Add(decision with { CorrelationId = correlationId });
                }
            }
            catch (Exception ex)
            {
                failed.Add(request.Id);
                var stage = ex is FeedUnavailableException ? "resolve-feed-unavailable" : "resolve";
                await failureRecorder.RecordAsync(request.Id, stage, ex.Message, correlationId, cancellationToken);

                if (packageResolver is MultiFeedPackageResolver multiFeedResolver &&
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

        foreach (var resolved in resolutionResult.ResolvedPackages)
        {
            var transaction = await transactionCoordinator.ExecuteAsync(
                new(resolved.Id, resolved.Version, correlationId),
                cancellationToken);

            if (transaction.Succeeded)
            {
                applied.Add(resolved);
            }
            else
            {
                failed.Add(resolved.Id);
            }
        }

        return new(applied, failed);
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

        await failureRecorder.RecordAsync(packageId, "load", message, correlationId, cancellationToken);
    }
}
