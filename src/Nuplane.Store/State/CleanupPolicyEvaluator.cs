namespace Nuplane.Store.State;

/// <summary>
/// Describes the action taken or to be taken for a package version during cleanup.
/// </summary>
public enum CleanupAction
{
    /// <summary>The version is retained by cleanup policy.</summary>
    Kept,
    /// <summary>The version is eligible for deletion.</summary>
    Deleted,
    /// <summary>The deletion was blocked (e.g., the version is the last-known-good).</summary>
    Blocked
}

/// <summary>
/// Represents a package version entry used as input for cleanup evaluation.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="Version">The package version.</param>
/// <param name="CapturedAt">The time the version was first captured.</param>
/// <param name="IsLastKnownGood">Whether this version is the last-known-good version.</param>
public sealed record PackageVersionEntry(
    string PackageId,
    string Version,
    DateTimeOffset CapturedAt,
    bool IsLastKnownGood);

/// <summary>
/// Records the cleanup decision for a specific package version, including the action taken and the reason.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="Version">The package version.</param>
/// <param name="Action">The cleanup action taken.</param>
/// <param name="Reason">A machine-readable code describing the reason for the action.</param>
/// <param name="Timestamp">The time at which the decision was made.</param>
/// <param name="CorrelationId">The correlation identifier of the reconciliation cycle.</param>
public sealed record CleanupDecision(
    string PackageId,
    string Version,
    CleanupAction Action,
    string Reason,
    DateTimeOffset Timestamp,
    string CorrelationId);

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
