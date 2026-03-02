using Nuplane.Abstractions;

namespace Nuplane.NuGet.Resolution;

public sealed class MultiFeedResolverOptions
{
    public List<string> OrderedFeeds { get; } = [];

    public HashSet<string> UnavailableFeeds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool StopOnFirstUnavailable { get; set; }
}

public sealed class MultiFeedResolutionException(string packageId, string message)
    : InvalidOperationException($"Multi-feed resolution failed for '{packageId}': {message}");

public sealed class MultiFeedPackageResolver : INuGetPackageResolver
{
    private readonly MultiFeedResolverOptions options;

    public MultiFeedPackageResolver(MultiFeedResolverOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var candidateFeeds = BuildCandidateFeeds(request);
        if (candidateFeeds.Count == 0)
        {
            throw new MultiFeedResolutionException(request.Id, "No candidate feed is configured.");
        }

        foreach (var feed in candidateFeeds)
        {
            if (options.UnavailableFeeds.Contains(feed))
            {
                if (options.StopOnFirstUnavailable)
                {
                    throw new MultiFeedResolutionException(request.Id, $"Feed '{feed}' is unavailable.");
                }

                continue;
            }

            var version = SelectVersion(request.VersionRange);
            return Task.FromResult(new ResolvedPackage(
                request.Id,
                version,
                feed,
                $"/packages/{request.Id}/{version}",
                DateTimeOffset.UtcNow,
                request.SourceName));
        }

        throw new MultiFeedResolutionException(request.Id, "No candidate feed is available.");
    }

    private List<string> BuildCandidateFeeds(PackageRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.FeedName))
        {
            return [request.FeedName];
        }

        return options.OrderedFeeds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
