using System.Collections.Concurrent;
using Nuplane.Abstractions;
using Nuplane.NuGet.Resolution;
using Nuplane.Runtime.Configuration;

namespace Nuplane.Runtime.Reconciliation;

public sealed class FeedUnavailableException(string feedName, string packageId)
    : InvalidOperationException($"Feed '{feedName}' is unavailable for package '{packageId}'.")
{
    public string FeedName { get; } = feedName;

    public string PackageId { get; } = packageId;
}

public sealed class MultiFeedPackageResolver : INuGetPackageResolver
{
    private readonly FeedResolutionOptions options;
    private readonly FeedResolutionPolicy policy;
    private readonly ConcurrentDictionary<string, FeedResolutionDecision> decisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> attempts = new(StringComparer.OrdinalIgnoreCase);

    public MultiFeedPackageResolver(FeedResolutionOptions options, FeedResolutionPolicy policy)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        attempts.AddOrUpdate(request.Id, 1, (_, current) => current + 1);
        var candidates = policy.OrderCandidates(request);
        var candidateNames = candidates.Select(x => x.Name).ToArray();

        foreach (var candidate in candidates)
        {
            var unavailable = options.UnavailableFeeds.Contains(candidate.Name);
            if (unavailable)
            {
                var shouldStop = options.PolicyMode == FeedResolutionPolicyMode.Strict || options.StopOnFirstSuccessfulFeed;
                if (shouldStop)
                {
                    decisions[request.Id] = FeedResolutionDecision.Failed(
                        request,
                        candidateNames,
                        string.Empty,
                        "explicit-feed-or-strict-stop",
                        feedUnavailable: true,
                        $"Feed '{candidate.Name}' unavailable");

                    throw new FeedUnavailableException(candidate.Name, request.Id);
                }

                continue;
            }

            var selectedVersion = SelectVersion(request.VersionRange);
            var resolved = new ResolvedPackage(
                request.Id,
                selectedVersion,
                candidate.Name,
                $"/packages/{request.Id}/{selectedVersion}",
                DateTimeOffset.UtcNow,
                request.SourceName);

            decisions[request.Id] = FeedResolutionDecision.Resolved(
                request,
                candidateNames,
                resolved,
                correlationId: string.Empty,
                decisionPath: "ordered-candidate-success");

            return Task.FromResult(resolved);
        }

        decisions[request.Id] = FeedResolutionDecision.Failed(
            request,
            candidateNames,
            correlationId: string.Empty,
            decisionPath: "no-available-candidates",
            feedUnavailable: true,
            failureReason: "No candidate feed was available.");

        throw new InvalidOperationException($"No available feed could resolve package '{request.Id}'.");
    }

    public bool TryGetDecision(string packageId, out FeedResolutionDecision decision) =>
        decisions.TryGetValue(packageId, out decision!);

    public int GetAttempts(string packageId) => attempts.TryGetValue(packageId, out var count) ? count : 0;

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
