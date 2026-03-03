namespace Nuplane.Abstractions;

/// <summary>
/// Provides a source of desired package requests for reconciliation.
/// Implementations read desired state from configuration files, APIs, or other inputs.
/// </summary>
public interface IDesiredPackageSource
{
    /// <summary>
    /// Returns the current list of desired package requests from this source.
    /// </summary>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The list of package requests representing the desired state.</returns>
    Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct);
}