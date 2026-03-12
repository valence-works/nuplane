namespace Nuplane.Store.Cleanup;

/// <summary>
/// Configuration options for the package version cleanup policy, controlling retention
/// by version count, age, execution mode, and last-known-good protection.
/// </summary>
public sealed class CleanupPolicyOptions
{
    /// <summary>
    /// Gets or sets the number of most recent versions to retain per package.
    /// </summary>
    public int? RetainLastNVersions { get; set; }

    /// <summary>
    /// Gets or sets the maximum age in days for retained versions.
    /// </summary>
    public int? RetainYoungerThanDays { get; set; }

    /// <summary>
    /// Gets or sets the cleanup execution mode.
    /// </summary>
    public CleanupExecutionMode Mode { get; set; } = CleanupExecutionMode.Automatic;

    /// <summary>
    /// Gets or sets whether the last-known-good version is always protected from cleanup.
    /// </summary>
    public bool ProtectLastKnownGood { get; set; } = true;

    /// <summary>
    /// Determines whether a version is retained by the union of count-based and age-based policies.
    /// </summary>
    /// <param name="versionOrdinalFromNewest">The 1-based ordinal from newest.</param>
    /// <param name="ageInDays">The age of the version in days.</param>
    /// <returns><see langword="true"/> if the version should be retained; otherwise <see langword="false"/>.</returns>
    public bool IsRetainedByUnion(int versionOrdinalFromNewest, int ageInDays)
    {
        var keepByCount = RetainLastNVersions.HasValue && versionOrdinalFromNewest <= RetainLastNVersions.Value;
        var keepByAge = RetainYoungerThanDays.HasValue && ageInDays <= RetainYoungerThanDays.Value;

        if (!RetainLastNVersions.HasValue && !RetainYoungerThanDays.HasValue)
        {
            return true;
        }

        return keepByCount || keepByAge;
    }
}
