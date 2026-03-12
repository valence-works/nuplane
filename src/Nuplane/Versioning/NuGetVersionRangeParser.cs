using Nuplane.Feeds.Versioning;

namespace Nuplane.Versioning;

/// <summary>
/// Parses NuGet version range expressions and extracts concrete version strings.
/// Used only for local directory feeds where nupkg files are already on disk and
/// a concrete version is needed to locate the file. Remote feed version selection
/// is handled by <see cref="IVersionRangeEvaluator"/>.
/// </summary>
internal static class NuGetVersionRangeParser
{
    /// <summary>
    /// Extracts a concrete version string from a NuGet version range expression.
    /// For exact versions (e.g., "[1.0.0]" or "1.0.0"), returns the version as-is.
    /// For range expressions (e.g., "[1.0.0, 2.0.0)"), returns the lower bound.
    /// Returns "0.0.0" for empty or null input.
    /// </summary>
    public static string SelectVersion(string versionRange)
    {
        if (string.IsNullOrWhiteSpace(versionRange))
        {
            return "0.0.0";
        }

        var normalized = versionRange.Trim();
        if (normalized.StartsWith("[", StringComparison.Ordinal) || normalized.StartsWith("(", StringComparison.Ordinal))
        {
            var parts = normalized
                .TrimStart('[', '(')
                .TrimEnd(']', ')')
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length > 0)
            {
                return parts[0];
            }
        }

        return normalized;
    }
}

