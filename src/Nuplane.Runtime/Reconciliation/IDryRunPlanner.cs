using Nuplane.Abstractions;

namespace Nuplane.Runtime.Reconciliation;

/// <summary>
/// Builds a dry-run plan that previews what changes a reconciliation cycle would make
/// without actually mutating state.
/// </summary>
public interface IDryRunPlanner
{
    /// <summary>
    /// Builds a dry-run plan showing the changes that would result from applying the desired packages.
    /// </summary>
    /// <param name="desired">The resolved desired packages.</param>
    /// <param name="activeVersions">The currently active package versions.</param>
    /// <param name="correlationId">The correlation identifier for this reconciliation cycle.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A dry-run plan with the projected change set.</returns>
    Task<DryRunPlan> BuildPlanAsync(
        IReadOnlyCollection<ResolvedPackage> desired,
        IReadOnlyDictionary<string, string> activeVersions,
        string correlationId,
        CancellationToken cancellationToken);
}
