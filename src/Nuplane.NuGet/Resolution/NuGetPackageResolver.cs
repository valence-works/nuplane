using Nuplane.Abstractions;

namespace Nuplane.NuGet.Resolution;

public interface INuGetPackageResolver
{
    Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken);
}

public sealed class NuGetPackageResolver : INuGetPackageResolver
{
    public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var selectedVersion = SelectVersion(request.VersionRange);
        var resolved = new ResolvedPackage(
            request.Id,
            selectedVersion,
            request.FeedName ?? "default",
            $"/packages/{request.Id}/{selectedVersion}",
            DateTimeOffset.UtcNow,
            request.SourceName);

        return Task.FromResult(resolved);
    }

    private static string SelectVersion(string versionRange)
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