using Nuplane.Abstractions;

namespace Nuplane.Loading;

/// <summary>
/// Tracks per-cycle package loading failures so the reconciliation runtime can surface
/// loader failures in health and observability without taking a compile-time dependency
/// on the loading hosting package implementation.
/// </summary>
public interface ILoadingFailureTracker : ICycleFailureContributor
{
    /// <summary>
    /// Records a single package loading failure for the specified reconciliation correlation.
    /// </summary>
    /// <param name="correlationId">The reconciliation correlation identifier.</param>
    /// <param name="packageId">The failed package identifier.</param>
    /// <param name="reason">An optional secret-safe diagnostic message.</param>
    void RecordFailure(string correlationId, string packageId, string? reason = null);
}
