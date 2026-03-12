using Nuplane.Abstractions;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Reconciliation;

/// <summary>
/// Evaluates a resolved package against the lock file to determine whether
/// the package version and source should be enforced or overridden.
/// </summary>
public interface ILockFileCoordinator
{
    /// <summary>
    /// Evaluates the specified resolved package against the lock file.
    /// </summary>
    /// <param name="resolved">The resolved package to evaluate.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The lock file evaluation result, including the effective package and hash expectation.</returns>
    Task<LockFileEvaluationResult> EvaluateAsync(ResolvedPackage resolved, CancellationToken cancellationToken);
}
