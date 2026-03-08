namespace Nuplane.Runtime.Feeds.Versioning;

/// <summary>
/// Evaluates a version range against a list of available versions and selects the best match.
/// </summary>
public interface IVersionRangeEvaluator
{
    /// <summary>
    /// Selects the best matching version from the available versions for the given range.
    /// </summary>
    /// <param name="versionRange">The version range string, or empty/null for "resolve to latest stable".</param>
    /// <param name="availableVersions">The available version strings.</param>
    /// <returns>The resolution result indicating the selected version or failure reason.</returns>
    VersionResolutionResult SelectBestMatch(string versionRange, IReadOnlyList<string> availableVersions);

    /// <summary>
    /// Validates whether the given string is a syntactically valid version range.
    /// </summary>
    /// <param name="versionRange">The version range string to validate.</param>
    /// <returns><see langword="true"/> if the range is valid or empty; <see langword="false"/> otherwise.</returns>
    bool IsValidRange(string versionRange);
}
