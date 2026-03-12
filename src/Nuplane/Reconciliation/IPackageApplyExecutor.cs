using Nuplane.Abstractions;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Reconciliation;

/// <summary>
/// Resolves package requests and executes transactional package activation,
/// coordinating between package resolution, transaction execution, and failure recording.
/// </summary>
public interface IPackageApplyExecutor
{
    /// <summary>
    /// Resolves desired package requests to concrete packages using the configured resolver.
    /// </summary>
    /// <param name="desiredRequests">The package requests to resolve.</param>
    /// <param name="correlationId">The correlation identifier for this cycle.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The resolution result containing resolved packages, failures, and feed decisions.</returns>
    Task<PackageResolutionResult> ResolveAsync(
        IReadOnlyList<PackageRequest> desiredRequests,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executes transactional activation for resolved packages.
    /// </summary>
    /// <param name="resolutionResult">The resolution result from <see cref="ResolveAsync"/>.</param>
    /// <param name="correlationId">The correlation identifier for this cycle.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The apply execution result containing applied packages and failures.</returns>
    Task<PackageApplyExecutionResult> ExecuteTransactionsAsync(
        PackageResolutionResult resolutionResult,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records a loading failure for a package without mutating the active package state.
    /// </summary>
    /// <param name="packageId">The package that failed to load.</param>
    /// <param name="correlationId">The correlation identifier for this cycle.</param>
    /// <param name="message">The failure message.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task RecordLoadingFailureNonMutatingAsync(
        string packageId,
        string correlationId,
        string message,
        CancellationToken cancellationToken);
}
