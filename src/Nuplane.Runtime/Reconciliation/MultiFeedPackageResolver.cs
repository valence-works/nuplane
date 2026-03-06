using System.Collections.Concurrent;
using System.IO.Compression;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Versioning;
using Nuplane.Runtime.Reconciliation.Models;
using Nuplane.Runtime.Reconciliation.FeedPolicy;

namespace Nuplane.Runtime.Reconciliation;


/// <summary>
/// Resolves packages across multiple feeds using priority ordering and feed availability
/// tracking, recording resolution decisions for observability.
/// </summary>
public sealed class MultiFeedPackageResolver(IOptions<FeedResolutionOptions> options, FeedResolutionPolicy policy) : IPackageResolver
{
    private readonly FeedResolutionOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly FeedResolutionPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly ConcurrentDictionary<string, FeedResolutionDecision> _decisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        _attempts.AddOrUpdate(request.Id, 1, (_, current) => current + 1);
        var candidates = _policy.OrderCandidates(request);
        var candidateNames = candidates.Select(x => x.Name).ToArray();

        foreach (var candidate in candidates)
        {
            var unavailable = _options.UnavailableFeeds.Contains(candidate.Name);
            if (unavailable)
            {
                var shouldStop = _options.PolicyMode == FeedResolutionPolicyMode.Strict || _options.StopOnFirstSuccessfulFeed;
                if (shouldStop)
                {
                    _decisions[request.Id] = FeedResolutionDecision.Failed(
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

            var selectedVersion = NuGetVersionRangeParser.SelectVersion(request.VersionRange);
            var installPath = ResolveInstallPath(candidate, request.Id, selectedVersion);
            var resolved = new ResolvedPackage(
                request.Id,
                selectedVersion,
                candidate.Name,
                installPath,
                DateTimeOffset.UtcNow,
                request.SourceName);

            _decisions[request.Id] = FeedResolutionDecision.Resolved(
                request,
                candidateNames,
                resolved,
                correlationId: string.Empty,
                decisionPath: "ordered-candidate-success");

            return Task.FromResult(resolved);
        }

        _decisions[request.Id] = FeedResolutionDecision.Failed(
            request,
            candidateNames,
            correlationId: string.Empty,
            decisionPath: "no-available-candidates",
            feedUnavailable: true,
            failureReason: "No candidate feed was available.");

        throw new NoEligibleFeedException(request.Id, "No candidate feed was available.");
    }

    /// <summary>
    /// Resolves the install path for a package from the specified feed.
    /// For local directory feeds (<c>file://</c> scheme), the nupkg is extracted to an install directory.
    /// For remote feeds, a synthetic path is returned (to be populated by a future acquisition step).
    /// </summary>
    private static string ResolveInstallPath(FeedDefinition feed, string packageId, string version)
    {
        if (!IsLocalDirectoryFeed(feed))
        {
            return $"/packages/{packageId}/{version}";
        }

        var feedDirectoryPath = feed.ServiceIndex.LocalPath;
        var nupkgFileName = $"{packageId}.{version}.nupkg";
        var nupkgPath = Path.Combine(feedDirectoryPath, nupkgFileName);

        if (!File.Exists(nupkgPath))
        {
            throw new FileNotFoundException(
                $"Expected nupkg '{nupkgFileName}' was not found in local directory feed '{feed.Name}' at '{feedDirectoryPath}'.",
                nupkgPath);
        }

        var installDir = Path.Combine(feedDirectoryPath, ".installed", packageId, version);

        if (!Directory.Exists(installDir))
        {
            Directory.CreateDirectory(installDir);
            ZipFile.ExtractToDirectory(nupkgPath, installDir, overwriteFiles: true);
        }

        return installDir;
    }

    /// <summary>
    /// Determines whether a feed definition represents a local directory feed.
    /// </summary>
    private static bool IsLocalDirectoryFeed(FeedDefinition feed) =>
        feed.ServiceIndex.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tries to retrieve the feed resolution decision for the specified package.
    /// </summary>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="decision">The resolution decision, if found.</param>
    /// <returns><see langword="true"/> if a decision was found; otherwise <see langword="false"/>.</returns>
    public bool TryGetDecision(string packageId, out FeedResolutionDecision decision) =>
        _decisions.TryGetValue(packageId, out decision!);

    /// <summary>
    /// Gets the number of resolution attempts for the specified package.
    /// </summary>
    /// <param name="packageId">The package identifier.</param>
    /// <returns>The number of attempts, or 0 if no attempts have been made.</returns>
    public int GetAttempts(string packageId) => _attempts.GetValueOrDefault(packageId, 0);

}
