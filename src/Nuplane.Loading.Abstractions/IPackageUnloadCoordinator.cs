namespace Nuplane.Loading;

/// <summary>
/// Coordinates the unloading of package assembly load contexts, including deactivation
/// timeouts and GC-based collectibility verification.
/// </summary>
public interface IPackageUnloadCoordinator
{
    /// <summary>
    /// Attempts to deactivate and unload a package's assembly load context.
    /// </summary>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="context">The load context handle to unload.</param>
    /// <param name="deactivationTimeout">The maximum time to wait for deactivation.</param>
    /// <param name="correlationId">The correlation identifier of the reconciliation cycle.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A tuple of the deactivation attempt details and the unload outcome record.</returns>
    Task<(DeactivationAttempt deactivation, UnloadOutcomeRecord unload)> AttemptUnloadAsync(
        string packageId,
        PackageLoadContextHandle context,
        TimeSpan deactivationTimeout,
        string correlationId,
        CancellationToken cancellationToken);
}