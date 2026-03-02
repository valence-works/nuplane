using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;

namespace Nuplane.Runtime.Reconciliation;

public sealed class FeedResolutionPolicy
{
    private readonly FeedResolutionOptions options;

    public FeedResolutionPolicy(FeedResolutionOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public IReadOnlyList<FeedDefinition> OrderCandidates(PackageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(request.FeedName))
        {
            var explicitFeed = options.Feeds
                .FirstOrDefault(x => string.Equals(x.Name, request.FeedName, StringComparison.OrdinalIgnoreCase));

            return explicitFeed is null ? [] : [explicitFeed];
        }

        return options.Feeds
            .OrderBy(x => options.GetPriority(x.Name))
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ResolvedPackage SelectWinningPackage(IReadOnlyList<ResolvedPackage> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("No resolved candidates are available for selection.");
        }

        return candidates
            .OrderByDescending(x => VersionKey.Create(x.Version))
            .ThenBy(x => x.FeedName, StringComparer.OrdinalIgnoreCase)
            .First();
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
            if (normalized.StartsWith("[", StringComparison.Ordinal) || normalized.StartsWith("(", StringComparison.Ordinal))
            {
                normalized = normalized.TrimStart('[', '(').TrimEnd(']', ')');
                normalized = normalized.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "0.0.0";
            }

            var plusIndex = normalized.IndexOf('+');
            if (plusIndex >= 0)
            {
                normalized = normalized[..plusIndex];
            }

            var dashIndex = normalized.IndexOf('-');
            var core = dashIndex >= 0 ? normalized[..dashIndex] : normalized;
            var suffix = dashIndex >= 0 ? normalized[(dashIndex + 1)..] : string.Empty;

            var parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries);
            _ = int.TryParse(parts.ElementAtOrDefault(0), out var major);
            _ = int.TryParse(parts.ElementAtOrDefault(1), out var minor);
            _ = int.TryParse(parts.ElementAtOrDefault(2), out var patch);

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
