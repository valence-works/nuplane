using Nuplane.Abstractions;

namespace Nuplane.Runtime.Reconciliation;

/// <summary>
/// Computes the difference between desired and actual (active) package state,
/// producing a change set of added, updated, and removed packages.
/// </summary>
public interface IDesiredActualDiffEngine
{
    /// <summary>
    /// Computes the package change set by comparing desired packages against active versions.
    /// </summary>
    /// <param name="desired">The resolved desired packages.</param>
    /// <param name="activeVersions">The currently active package versions.</param>
    /// <param name="correlationId">The correlation identifier for this reconciliation cycle.</param>
    /// <param name="timestamp">The timestamp for the change set.</param>
    /// <returns>A change set describing added, updated, and removed packages.</returns>
    PackageChangeSet Compute(
        IReadOnlyCollection<ResolvedPackage> desired,
        IReadOnlyDictionary<string, string> activeVersions,
        string correlationId,
        DateTimeOffset timestamp);

    /// <summary>
    /// Builds a dictionary of package identifiers to versions from the desired package list.
    /// </summary>
    /// <param name="desired">The resolved desired packages.</param>
    /// <returns>A dictionary mapping package identifiers to their resolved versions.</returns>
    IReadOnlyDictionary<string, string> BuildNextActiveVersions(IReadOnlyCollection<ResolvedPackage> desired);
}
