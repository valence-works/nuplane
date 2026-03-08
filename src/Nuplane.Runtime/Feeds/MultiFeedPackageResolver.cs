using System.Collections.Concurrent;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Runtime.Feeds.Configuration;
using Nuplane.Runtime.Feeds.Policy;
using Nuplane.Runtime.Feeds.Versioning;
using Nuplane.Runtime.Reconciliation.Models;
using Nuplane.Runtime.Versioning;

namespace Nuplane.Runtime.Feeds;

/// <summary>
/// Resolves packages across multiple feeds using priority ordering and feed availability
/// tracking, recording resolution decisions for observability.
/// </summary>
public sealed class MultiFeedPackageResolver : IPackageResolver
{
    private readonly FeedResolutionOptions _options;
    private readonly FeedResolutionPolicy _policy;
    private readonly IRemotePackageAcquirer _remotePackageAcquirer;
    private readonly IFeedVersionEnumerator _versionEnumerator;
    private readonly IVersionRangeEvaluator _versionRangeEvaluator;
    private readonly ILogger<MultiFeedPackageResolver> _logger;
    private readonly ConcurrentDictionary<string, FeedResolutionDecision> _decisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);

    internal MultiFeedPackageResolver(
        IOptions<FeedResolutionOptions> options,
        FeedResolutionPolicy policy,
        IRemotePackageAcquirer remotePackageAcquirer,
        IFeedVersionEnumerator versionEnumerator,
        IVersionRangeEvaluator versionRangeEvaluator,
        ILogger<MultiFeedPackageResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _remotePackageAcquirer = remotePackageAcquirer ?? throw new ArgumentNullException(nameof(remotePackageAcquirer));
        _versionEnumerator = versionEnumerator ?? throw new ArgumentNullException(nameof(versionEnumerator));
        _versionRangeEvaluator = versionRangeEvaluator ?? throw new ArgumentNullException(nameof(versionRangeEvaluator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
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

            var selectedVersion = await ResolveVersionAsync(candidate, request, cancellationToken);
            if (selectedVersion is null)
            {
                continue;
            }

            var installPath = await ResolveInstallPathAsync(candidate, request.Id, selectedVersion.Value.Version, cancellationToken);
            var resolved = new ResolvedPackage(
                request.Id,
                selectedVersion.Value.Version,
                candidate.Name,
                installPath,
                DateTimeOffset.UtcNow,
                request.SourceName);

            _decisions[request.Id] = FeedResolutionDecision.Resolved(
                request,
                candidateNames,
                resolved,
                correlationId: string.Empty,
                decisionPath: "ordered-candidate-success",
                EnumeratedVersionCount: selectedVersion.Value.EnumeratedCount,
                CacheHit: selectedVersion.Value.CacheHit);

            return resolved;
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
    /// Resolves the concrete version for a package from the specified feed.
    /// For local directory feeds, the version is extracted from the request's range directly.
    /// For remote feeds, version enumeration and range evaluation are used.
    /// </summary>
    private async Task<VersionSelection?> ResolveVersionAsync(
        FeedDefinition feed,
        PackageRequest request,
        CancellationToken cancellationToken)
    {
        // Local directory feeds have exact versions already specified in the package request.
        if (IsLocalDirectoryFeed(feed))
        {
            var localVersion = NuGetVersionRangeParser.SelectVersion(request.VersionRange);
            return new VersionSelection(localVersion, 0, CacheHit: false);
        }

        var versionList = await _versionEnumerator.EnumerateVersionsAsync(feed, request.Id, cancellationToken);

        var result = _versionRangeEvaluator.SelectBestMatch(request.VersionRange, versionList.Versions);

        _logger.LogDebug(
            "Version resolution for {PackageId} on feed {FeedName}: range={VersionRange}, selected={SelectedVersion}, candidates={CandidateCount}",
            request.Id,
            feed.Name,
            request.VersionRange,
            result.SelectedVersion ?? "(none)",
            result.CandidateCount);

        if (!result.Success)
        {
            _logger.LogWarning(
                "Version resolution failed for {PackageId} on feed {FeedName}: {FailureReason}",
                request.Id,
                feed.Name,
                result.FailureReason);
            return null;
        }

        return new VersionSelection(result.SelectedVersion!, result.CandidateCount, CacheHit: false);
    }

    internal readonly record struct VersionSelection(string Version, int EnumeratedCount, bool CacheHit);

    /// <summary>
    /// Resolves the install path for a package from the specified feed.
    /// Local directory feeds extract the <c>.nupkg</c> into a stable install directory;
    /// remote feeds are downloaded and extracted via the configured remote package acquirer.
    /// </summary>
    private async Task<string> ResolveInstallPathAsync(FeedDefinition feed, string packageId, string version, CancellationToken cancellationToken)
    {
        if (!IsLocalDirectoryFeed(feed))
        {
            return await _remotePackageAcquirer.AcquireAsync(feed, packageId, version, cancellationToken);
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
        var completionMarkerPath = Path.Combine(installDir, ".nuplane-ready");

        if (!File.Exists(completionMarkerPath))
        {
            if (Directory.Exists(installDir))
            {
                Directory.Delete(installDir, recursive: true);
            }

            Directory.CreateDirectory(installDir);
            ZipFile.ExtractToDirectory(nupkgPath, installDir, overwriteFiles: true);
            await File.WriteAllTextAsync(completionMarkerPath, string.Empty, cancellationToken);
        }

        return installDir;
    }

    private static bool IsLocalDirectoryFeed(FeedDefinition feed) =>
        feed.ServiceIndex.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tries to retrieve the feed resolution decision for the specified package.
    /// </summary>
    public bool TryGetDecision(string packageId, out FeedResolutionDecision decision) =>
        _decisions.TryGetValue(packageId, out decision!);

    /// <summary>
    /// Gets the number of resolution attempts for the specified package.
    /// </summary>
    public int GetAttempts(string packageId) => _attempts.GetValueOrDefault(packageId, 0);

}

