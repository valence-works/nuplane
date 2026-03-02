using Nuplane.Abstractions;
using Nuplane.NuGet.Resolution;
using Nuplane.Store.State;
using Nuplane.Store.Transactions;

namespace Nuplane.Runtime.Reconciliation;

public sealed record PackageResolutionResult(
    IReadOnlyList<ResolvedPackage> ResolvedPackages,
    IReadOnlyList<string> FailedPackageIds);

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

    public async Task<PackageResolutionResult> ResolveAsync(
        IReadOnlyList<PackageRequest> desiredRequests,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desiredRequests);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var resolved = new List<ResolvedPackage>();
        var failed = new List<string>();

        foreach (var request in desiredRequests)
        {
            try
            {
                var pkg = await retryPolicy.ExecuteAsync(
                    ct => packageResolver.ResolveAsync(request, ct),
                    cancellationToken);
                resolved.Add(pkg);
            }
            catch (Exception ex)
            {
                failed.Add(request.Id);
                await failureRecorder.RecordAsync(request.Id, "resolve", ex.Message, correlationId, cancellationToken);
            }
        }

        return new PackageResolutionResult(resolved, failed);
    }

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
                new PackageTransactionRequest(resolved.Id, resolved.Version, correlationId),
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

        return new PackageApplyExecutionResult(applied, failed);
    }
}
