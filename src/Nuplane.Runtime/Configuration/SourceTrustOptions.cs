namespace Nuplane.Runtime.Configuration;

/// <summary>
/// Configuration options for source and package allowlisting, controlling which desired-state
/// sources and package identifiers are permitted during reconciliation.
/// </summary>
public sealed class SourceTrustOptions
{
    /// <summary>
    /// Gets the set of source names that are allowed to contribute desired-state requests.
    /// An empty set means all sources are allowed.
    /// </summary>
    public HashSet<string> AllowedSourceNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the set of package identifiers that are explicitly allowed.
    /// </summary>
    public HashSet<string> AllowedPackageIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets whether packages not in <see cref="AllowedPackageIds"/> are rejected.
    /// </summary>
    public bool RejectUnallowlistedPackages { get; init; } = true;

    /// <summary>
    /// Gets or sets whether runtime credential resolution (e.g., secret references) is permitted.
    /// </summary>
    public bool AllowRuntimeCredentialResolution { get; init; } = true;

    /// <summary>
    /// Determines whether the specified source name is allowed.
    /// </summary>
    /// <param name="sourceName">The source name to check.</param>
    /// <returns><see langword="true"/> if the source is allowed; otherwise <see langword="false"/>.</returns>
    public bool IsSourceAllowed(string sourceName)
    {
        if (AllowedSourceNames.Count == 0)
        {
            return true;
        }

        return AllowedSourceNames.Contains(sourceName);
    }

    /// <summary>
    /// Determines whether the specified package identifier is allowed.
    /// </summary>
    /// <param name="packageId">The package identifier to check.</param>
    /// <returns><see langword="true"/> if the package is allowed; otherwise <see langword="false"/>.</returns>
    public bool IsPackageAllowed(string packageId) =>
        !RejectUnallowlistedPackages
        || PackagePatternMatcher.MatchesAny(AllowedPackageIds, packageId);
}
