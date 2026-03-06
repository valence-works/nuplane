using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Nuplane.Abstractions;

/// <summary>
/// Provides wildcard pattern matching for package identifiers.
/// Supports <c>*</c> (matches any sequence of characters) and <c>?</c> (matches any single character).
/// Patterns without wildcards are matched exactly, case-insensitively.
/// Compiled <see cref="Regex"/> instances are cached by pattern string to avoid redundant compilation.
/// </summary>
public static class PackagePatternMatcher
{
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="packageId"/> satisfies
    /// at least one pattern in <paramref name="patterns"/>.
    /// </summary>
    /// <param name="patterns">The collection of patterns to test against.</param>
    /// <param name="packageId">The package identifier to test.</param>
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
    /// <param name="pattern">
    /// The pattern to match. Use <c>*</c> for any sequence of characters and <c>?</c> for any
    /// single character. Patterns without wildcards are matched exactly (case-insensitive).
    /// </param>
    /// <param name="packageId">The package identifier to test.</param>
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

        var regex = RegexCache.GetOrAdd(pattern, static p =>
        {
            var regexPattern = "^" + Regex.Escape(p)
                .Replace("\\*", ".*", StringComparison.Ordinal)
                .Replace("\\?", ".", StringComparison.Ordinal) + "$";

            return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        });

        return regex.IsMatch(packageId);
    }
}
