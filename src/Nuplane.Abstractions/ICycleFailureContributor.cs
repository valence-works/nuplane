namespace Nuplane.Abstractions;

/// <summary>
/// Provides a seam for external modules (e.g. assembly loading) to report
/// per-cycle package failures back into the reconciliation pipeline without
/// requiring the core to take a compile-time dependency on the module.
/// </summary>
public interface ICycleFailureContributor
{
    /// <summary>
    /// Returns the recorded failure package identifiers for the specified
    /// correlation and clears them from the contributor.
    /// </summary>
    IReadOnlyList<string> TakeFailedPackageIds(string correlationId);
}

