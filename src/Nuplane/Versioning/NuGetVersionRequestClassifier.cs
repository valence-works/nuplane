using NuGet.Versioning;

namespace Nuplane.Versioning;

internal enum NuGetVersionRequestKind
{
    EmptyOrLatest,
    Exact,
    RangeOrFloating
}

internal readonly record struct NuGetVersionRequest(NuGetVersionRequestKind Kind, string? ExactVersion)
{
    public bool IsExact => Kind == NuGetVersionRequestKind.Exact && !string.IsNullOrWhiteSpace(ExactVersion);
}

internal static class NuGetVersionRequestClassifier
{
    public static NuGetVersionRequest Classify(string? versionRange)
    {
        if (string.IsNullOrWhiteSpace(versionRange))
        {
            return new(NuGetVersionRequestKind.EmptyOrLatest, null);
        }

        var normalized = versionRange.Trim();
        if (IsBareExactVersion(normalized, out var bareVersion))
        {
            return new(NuGetVersionRequestKind.Exact, bareVersion);
        }

        if (VersionRange.TryParse(normalized, out var range) && IsExactRange(range))
        {
            return new(NuGetVersionRequestKind.Exact, range.MinVersion!.ToNormalizedString());
        }

        return new(NuGetVersionRequestKind.RangeOrFloating, null);
    }

    private static bool IsBareExactVersion(string value, out string exactVersion)
    {
        exactVersion = string.Empty;
        if (value.StartsWith("[", StringComparison.Ordinal) || value.StartsWith("(", StringComparison.Ordinal))
        {
            return false;
        }

        if (!NuGetVersion.TryParse(value, out var version))
        {
            return false;
        }

        exactVersion = version.ToNormalizedString();
        return true;
    }

    private static bool IsExactRange(VersionRange range) =>
        range.HasLowerAndUpperBounds
        && range.IsMinInclusive
        && range.IsMaxInclusive
        && range.MinVersion is not null
        && range.MaxVersion is not null
        && range.MinVersion == range.MaxVersion;
}
