using NuGet.Versioning;

namespace Nuplane.Feeds.Versioning;

/// <summary>
/// Evaluates NuGet version ranges using the NuGet.Versioning library and selects the best match.
/// </summary>
public sealed class NuGetVersionRangeEvaluator : IVersionRangeEvaluator
{
    /// <inheritdoc />
    public VersionResolutionResult SelectBestMatch(string versionRange, IReadOnlyList<string> availableVersions)
    {
        if (availableVersions.Count == 0)
        {
            return new(false, null, 0, "Resolution failed: no versions available.");
        }

        var parsed = new List<NuGetVersion>();
        foreach (var v in availableVersions)
        {
            if (NuGetVersion.TryParse(v, out var nugetVersion))
            {
                parsed.Add(nugetVersion);
            }
        }

        int candidateCount = availableVersions.Count;

        // Empty or null range → resolve to the latest stable version.
        if (string.IsNullOrWhiteSpace(versionRange))
        {
            var stable = parsed.Where(v => !v.IsPrerelease).ToList();
            if (stable.Count == 0)
            {
                return new(false, null, candidateCount, "Resolution failed: no stable versions available.");
            }

            var max = stable.Max()!;
            return new(true, max.ToNormalizedString(), candidateCount, null);
        }

        // Bare version (e.g., "2.0.0") → treat as exact match [x.y.z] per Nuplane convention (R-002).
        if (NuGetVersion.TryParse(versionRange, out _) && !versionRange.StartsWith('[') && !versionRange.StartsWith('('))
        {
            versionRange = $"[{versionRange}]";
        }

        if (!VersionRange.TryParse(versionRange, out var range))
        {
            return new(false, null, candidateCount, $"Resolution failed: invalid version range '{versionRange}'.");
        }

        // Filter to versions satisfying the range, then select the highest match.
        // NuGet's FindBestMatch returns the lowest satisfying version (dependency resolution),
        // but Nuplane resolves to the highest matching version per spec.
        var satisfying = parsed.Where(v => range.Satisfies(v)).ToList();
        if (satisfying.Count == 0)
        {
            return new(false, null, candidateCount, $"Resolution failed: no version matched range '{versionRange}'.");
        }

        var best = satisfying.Max()!;
        return new(true, best.ToNormalizedString(), candidateCount, null);
    }

    /// <inheritdoc />
    public bool IsValidRange(string versionRange)
    {
        if (string.IsNullOrWhiteSpace(versionRange))
        {
            return true;
        }

        // Bare version is valid per Nuplane convention.
        if (NuGetVersion.TryParse(versionRange, out _))
        {
            return true;
        }

        return VersionRange.TryParse(versionRange, out _);
    }
}
