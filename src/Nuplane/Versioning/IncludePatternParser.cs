namespace Nuplane.Versioning;

/// <summary>
/// Represents a parsed <c>IncludePatterns</c> entry split into its package glob and optional version range.
/// </summary>
/// <param name="PackageGlob">The package identity glob pattern (e.g., <c>MyPackage</c>, <c>MyPackage.*</c>, <c>*</c>).</param>
/// <param name="VersionRange">The version range suffix, or empty string for "resolve to latest".</param>
/// <param name="OriginalPattern">The original unparsed pattern string for diagnostics.</param>
internal sealed record ParsedIncludePattern(
    string PackageGlob,
    string VersionRange,
    string OriginalPattern);

/// <summary>
/// Splits an <c>IncludePatterns</c> entry into a package identity glob and an optional version range suffix.
/// </summary>
internal static class IncludePatternParser
{
    /// <summary>
    /// Parses a single <c>IncludePatterns</c> entry.
    /// </summary>
    /// <param name="pattern">The raw include pattern string.</param>
    /// <returns>A parsed pattern with separated glob and version range components.</returns>
    public static ParsedIncludePattern Parse(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return new(string.Empty, string.Empty, pattern ?? string.Empty);
        }

        var trimmed = pattern.Trim();

        // Strategy: find the first bracket/paren that starts a version range expression.
        // If found, everything before it (trimmed) is the package glob, and the bracket 
        // expression through end is the version range.
        var bracketIndex = IndexOfVersionRangeBracket(trimmed);
        if (bracketIndex > 0)
        {
            var glob = trimmed[..bracketIndex].TrimEnd();
            var versionRange = trimmed[bracketIndex..];
            return new(glob, versionRange, pattern);
        }

        // No bracket range found — check for a bare version at the end (digit-started suffix).
        var lastSpaceIndex = trimmed.LastIndexOf(' ');
        if (lastSpaceIndex < 0)
        {
            return new(trimmed, string.Empty, pattern);
        }

        var suffix = trimmed[(lastSpaceIndex + 1)..];
        if (suffix.Length > 0 && char.IsAsciiDigit(suffix[0]))
        {
            var glob = trimmed[..lastSpaceIndex].TrimEnd();
            return new(glob, suffix, pattern);
        }

        // Trailing segment doesn't look like a version range — entire string is the glob.
        return new(trimmed, string.Empty, pattern);
    }

    /// <summary>
    /// Finds the index of the first <c>[</c> or <c>(</c> character that likely starts a version range,
    /// but only if preceded by whitespace (i.e. it's a separate token, not part of the package glob).
    /// </summary>
    private static int IndexOfVersionRangeBracket(string value)
    {
        for (var i = 1; i < value.Length; i++)
        {
            if (value[i] is '[' or '(' && char.IsWhiteSpace(value[i - 1]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Attempts to determine whether a candidate string looks like a version range.
    /// </summary>
    internal static bool TryParseVersionRange(string candidate, out string versionRange)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            versionRange = string.Empty;
            return false;
        }

        var trimmed = candidate.Trim();
        if (trimmed.Length > 0 && IsVersionRangeStart(trimmed[0]))
        {
            versionRange = trimmed;
            return true;
        }

        versionRange = string.Empty;
        return false;
    }

    private static bool IsVersionRangeStart(char c) => c is '[' or '(' or (>= '0' and <= '9');
}
