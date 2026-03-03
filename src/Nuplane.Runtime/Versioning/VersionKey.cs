namespace Nuplane.Runtime.Versioning;

/// <summary>
/// A lightweight, comparable representation of a SemVer-like version string.
/// Used internally for deterministic version ordering during reconciliation.
/// </summary>
internal readonly record struct VersionKey(int Major, int Minor, int Patch, string Suffix) : IComparable<VersionKey>
{
    /// <summary>
    /// Parses a version string (optionally wrapped in NuGet range brackets) into a <see cref="VersionKey"/>.
    /// </summary>
    public static VersionKey Create(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return new(0, 0, 0, string.Empty);
        }

        var normalized = version.Trim();

        // Strip NuGet range brackets/parens: "[1.0.0, 2.0.0)" → "1.0.0, 2.0.0"
        if (normalized.StartsWith("[", StringComparison.Ordinal) || normalized.StartsWith("(", StringComparison.Ordinal))
        {
            normalized = normalized.TrimStart('[', '(').TrimEnd(']', ')');
            normalized = normalized.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "0.0.0";
        }

        // Strip build metadata
        var plusIndex = normalized.IndexOf('+');
        if (plusIndex >= 0)
        {
            normalized = normalized[..plusIndex];
        }

        // Split core vs. pre-release suffix
        var dashIndex = normalized.IndexOf('-');
        var core = dashIndex >= 0 ? normalized[..dashIndex] : normalized;
        var suffix = dashIndex >= 0 ? normalized[(dashIndex + 1)..] : string.Empty;

        var parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries);
        _ = int.TryParse(parts.ElementAtOrDefault(0), out var major);
        _ = int.TryParse(parts.ElementAtOrDefault(1), out var minor);
        _ = int.TryParse(parts.ElementAtOrDefault(2), out var patch);

        return new(major, minor, patch, suffix);
    }

    /// <inheritdoc />
    public int CompareTo(VersionKey other)
    {
        var majorCompare = Major.CompareTo(other.Major);
        if (majorCompare != 0)
        {
            return majorCompare;
        }

        var minorCompare = Minor.CompareTo(other.Minor);
        if (minorCompare != 0)
        {
            return minorCompare;
        }

        var patchCompare = Patch.CompareTo(other.Patch);
        if (patchCompare != 0)
        {
            return patchCompare;
        }

        // Stable (no suffix) sorts higher than pre-release
        if (string.IsNullOrEmpty(Suffix) && !string.IsNullOrEmpty(other.Suffix))
        {
            return 1;
        }

        if (!string.IsNullOrEmpty(Suffix) && string.IsNullOrEmpty(other.Suffix))
        {
            return -1;
        }

        return string.Compare(Suffix, other.Suffix, StringComparison.OrdinalIgnoreCase);
    }
}

