using Nuplane.Abstractions;
using Nuplane.NuGet.Resolution;
using Nuplane.Store.State;
using Nuplane.Store.Transactions;

namespace Nuplane.Runtime.Reconciliation;

public sealed record PackageApplyExecutionResult(
    IReadOnlyList<ResolvedPackage> AppliedPackages,
    IReadOnlyList<string> FailedPackageIds);

public sealed class PackageApplyExecutor
{
    private readonly INuGetPackageResolver packageResolver;
    private readonly PackageTransactionCoordinator transactionCoordinator;
    private readonly ReconciliationRetryPolicy retryPolicy;
    private readonly FailureRecorder failureRecorder;

    public PackageApplyExecutor(
        INuGetPackageResolver packageResolver,
        PackageTransactionCoordinator transactionCoordinator,
        ReconciliationRetryPolicy retryPolicy,
        FailureRecorder failureRecorder)
    {
        this.packageResolver = packageResolver ?? throw new ArgumentNullException(nameof(packageResolver));
        this.transactionCoordinator = transactionCoordinator ?? throw new ArgumentNullException(nameof(transactionCoordinator));
        this.retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        this.failureRecorder = failureRecorder ?? throw new ArgumentNullException(nameof(failureRecorder));
    }

    public async Task<PackageApplyExecutionResult> ExecuteAsync(
        IReadOnlyList<PackageRequest> desiredRequests,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desiredRequests);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var applied = new List<ResolvedPackage>();
        var failed = new List<string>();

        foreach (var request in desiredRequests)
        {
            try
            {
                var resolved = await retryPolicy.ExecuteAsync(
                    ct => packageResolver.ResolveAsync(request, ct),
                    cancellationToken);

                var transaction = await transactionCoordinator.ExecuteAsync(
                    new PackageTransactionRequest(request.Id, resolved.Version, correlationId),
                    cancellationToken);

                if (transaction.Succeeded)
                {
                    applied.Add(resolved);
                }
                else
                {
                    failed.Add(request.Id);
                }
            }
            catch (Exception ex)
            {
                failed.Add(request.Id);
                await failureRecorder.RecordAsync(request.Id, "resolve", ex.Message, correlationId, cancellationToken);
            }
        }

        return new PackageApplyExecutionResult(applied, failed);
    }
}
