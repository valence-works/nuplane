using Nuplane.Store.State;

namespace Nuplane.Store.Cleanup;

/// <summary>
/// Evaluates cleanup policy rules for individual package versions, determining whether
/// each version should be kept, deleted, or is protected as last-known-good.
/// </summary>
public sealed class CleanupPolicyEvaluator
{
    /// <summary>
    /// Evaluates the cleanup policy for a specific package version.
    /// </summary>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="version">The package version.</param>
    /// <param name="capturedAt">The time the version was captured.</param>
    /// <param name="versionOrdinalFromNewest">The 1-based ordinal of this version from newest.</param>
    /// <param name="isLastKnownGood">Whether this version is the last-known-good version.</param>
    /// <param name="options">The cleanup policy options.</param>
    /// <param name="now">The current time for age calculations.</param>
    /// <param name="correlationId">The correlation identifier.</param>
    /// <returns>The cleanup decision for this version.</returns>
    public CleanupDecision Evaluate(
        string packageId,
        string version,
        DateTimeOffset capturedAt,
        int versionOrdinalFromNewest,
        bool isLastKnownGood,
        CleanupPolicyOptions options,
        DateTimeOffset now,
        string correlationId = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(options);

        if (options.ProtectLastKnownGood && isLastKnownGood)
        {
            return new(packageId, version, CleanupAction.Kept, "protected-lkg", now, correlationId);
        }

        var ageInDays = Math.Max(0, (int)(now - capturedAt).TotalDays);
        if (options.IsRetainedByUnion(versionOrdinalFromNewest, ageInDays))
        {
            return new(packageId, version, CleanupAction.Kept, "retained-policy", now, correlationId);
        }

        return new(packageId, version, CleanupAction.Deleted, "eligible-for-deletion", now, correlationId);
    }
}
