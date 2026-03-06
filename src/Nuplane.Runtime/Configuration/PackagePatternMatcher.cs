using System.Text.RegularExpressions;

namespace Nuplane.Runtime.Configuration;

/// <summary>
/// Provides wildcard pattern matching for package identifiers.
/// Supports <c>*</c> (matches any sequence of characters) and <c>?</c> (matches any single character).
/// Patterns without wildcards are matched exactly, case-insensitively.
/// </summary>
internal static class PackagePatternMatcher
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="packageId"/> satisfies
    /// at least one pattern in <paramref name="patterns"/>.
    /// </summary>
    public static bool MatchesAny(IEnumerable<string> patterns, string packageId)
    {
        foreach (var pattern in patterns)
        {
            if (IsMatch(pattern, packageId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="packageId"/> matches <paramref name="pattern"/>.
    /// </summary>
    public static bool IsMatch(string pattern, string packageId)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(packageId))
        {
            return false;
        }

        if (!pattern.Contains('*') && !pattern.Contains('?'))
        {
            return string.Equals(pattern, packageId, StringComparison.OrdinalIgnoreCase);
        }

        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";

        return Regex.IsMatch(packageId, regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
