using Nuplane.Abstractions;

namespace Nuplane.Runtime.Reconciliation;

public sealed class DesiredActualDiffEngine
{
    public PackageChangeSet Compute(
        IReadOnlyCollection<ResolvedPackage> desired,
        IReadOnlyDictionary<string, string> activeVersions,
        string correlationId,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(activeVersions);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var deduplicatedDesired = DeduplicateDesired(desired);

        var added = deduplicatedDesired
            .Where(pkg => !activeVersions.ContainsKey(pkg.Id))
            .OrderBy(pkg => pkg.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var updated = deduplicatedDesired
            .Where(pkg => activeVersions.TryGetValue(pkg.Id, out var activeVersion) && !string.Equals(activeVersion, pkg.Version, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pkg => pkg.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var removed = activeVersions.Keys
            .Where(activeId => deduplicatedDesired.All(pkg => !string.Equals(pkg.Id, activeId, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(activeId => activeId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new PackageChangeSet(added, updated, removed, correlationId, timestamp);
    }

    public IReadOnlyDictionary<string, string> BuildNextActiveVersions(IReadOnlyCollection<ResolvedPackage> desired)
    {
        ArgumentNullException.ThrowIfNull(desired);

        return DeduplicateDesired(desired)
            .OrderBy(pkg => pkg.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pkg => pkg.Id, pkg => pkg.Version, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ResolvedPackage> DeduplicateDesired(IReadOnlyCollection<ResolvedPackage> desired)
    {
        return desired
            .GroupBy(pkg => pkg.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(pkg => VersionKey.Create(pkg.Version))
                .ThenBy(pkg => pkg.SourceName, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(pkg => pkg.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private readonly record struct VersionKey(int Major, int Minor, int Patch, string Suffix) : IComparable<VersionKey>
    {
        public static VersionKey Create(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                return new VersionKey(0, 0, 0, string.Empty);
            }

            var normalized = version.Trim();
            if (normalized.StartsWith("[", StringComparison.Ordinal) && normalized.EndsWith("]", StringComparison.Ordinal))
            {
                normalized = normalized[1..^1];
            }

            var plusIndex = normalized.IndexOf('+');
            if (plusIndex >= 0)
            {
                normalized = normalized[..plusIndex];
            }

            var dashIndex = normalized.IndexOf('-');
            var core = dashIndex >= 0 ? normalized[..dashIndex] : normalized;
            var suffix = dashIndex >= 0 ? normalized[(dashIndex + 1)..] : string.Empty;

            var coreParts = core.Split('.', StringSplitOptions.RemoveEmptyEntries);
            _ = int.TryParse(coreParts.ElementAtOrDefault(0), out var major);
            _ = int.TryParse(coreParts.ElementAtOrDefault(1), out var minor);
            _ = int.TryParse(coreParts.ElementAtOrDefault(2), out var patch);

            return new VersionKey(major, minor, patch, suffix);
        }

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
}