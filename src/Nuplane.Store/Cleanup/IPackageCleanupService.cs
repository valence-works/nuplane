namespace Nuplane.Store.State;

/// <summary>
/// Defines the contract for executing automatic package version cleanup.
/// </summary>
public interface IPackageCleanupService
{
    /// <summary>
    /// Executes automatic cleanup based on the configured policy.
    /// </summary>
    /// <param name="packageVersions">The package versions to evaluate.</param>
    /// <param name="options">The cleanup policy options.</param>
    /// <param name="correlationId">The correlation identifier of the reconciliation cycle.</param>
    /// <param name="triggerOnSuccessfulReconciliation">Whether cleanup was triggered by a successful cycle.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The cleanup decisions for each evaluated version.</returns>
    Task<IReadOnlyList<CleanupDecision>> ExecuteAutomaticAsync(
        IReadOnlyList<PackageVersionEntry> packageVersions,
        CleanupPolicyOptions options,
        string correlationId,
        bool triggerOnSuccessfulReconciliation,
        CancellationToken cancellationToken);
}
