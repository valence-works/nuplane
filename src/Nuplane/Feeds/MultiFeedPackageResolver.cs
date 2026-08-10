using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Feeds.Configuration;
using Nuplane.Feeds.Policy;
using Nuplane.Feeds.Versioning;
using Nuplane.Observability;
using Nuplane.Reconciliation.Models;
using Nuplane.Versioning;
using NuGet.Versioning;

namespace Nuplane.Feeds;

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
    private readonly ReconciliationMetrics? _metrics;
    private readonly ConcurrentDictionary<string, FeedResolutionDecision> _decisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="MultiFeedPackageResolver"/> class.
    /// </summary>
    /// <param name="options">Feed resolution options.</param>
    /// <param name="policy">Feed ordering and failover policy.</param>
    /// <param name="remotePackageAcquirer">Downloads concrete package versions for remote feeds.</param>
    /// <param name="versionEnumerator">Enumerates candidate versions from feeds.</param>
    /// <param name="versionRangeEvaluator">Selects the best concrete version for a requested range.</param>
    /// <param name="logger">Logger for feed resolution diagnostics.</param>
    /// <param name="metrics">Optional metrics sink for version resolution outcomes.</param>
    public MultiFeedPackageResolver(
        IOptions<FeedResolutionOptions> options,
        FeedResolutionPolicy policy,
        IRemotePackageAcquirer remotePackageAcquirer,
        IFeedVersionEnumerator versionEnumerator,
        IVersionRangeEvaluator versionRangeEvaluator,
        ILogger<MultiFeedPackageResolver> logger,
        ReconciliationMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _remotePackageAcquirer = remotePackageAcquirer ?? throw new ArgumentNullException(nameof(remotePackageAcquirer));
        _versionEnumerator = versionEnumerator ?? throw new ArgumentNullException(nameof(versionEnumerator));
        _versionRangeEvaluator = versionRangeEvaluator ?? throw new ArgumentNullException(nameof(versionRangeEvaluator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics;
    }

    /// <inheritdoc />
    public async Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        _attempts.AddOrUpdate(request.Id, 1, (_, current) => current + 1);
        var candidates = _policy.OrderCandidates(request);
        var candidateNames = candidates.Select(x => x.Name).ToArray();
        FeedResolutionDecision? lastFailure = null;
        var seenFailureKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (IsRemoteFeedDisabled(candidate, request, candidateNames, out var disabledDecision))
            {
                lastFailure = Accumulate(lastFailure, disabledDecision, seenFailureKeys);
                _decisions[request.Id] = lastFailure;
                continue;
            }

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
            if (!selectedVersion.Success)
            {
                lastFailure = Accumulate(lastFailure, FeedResolutionDecision.Failed(
                    request,
                    candidateNames,
                    correlationId: string.Empty,
                    decisionPath: selectedVersion.DecisionPath,
                    feedUnavailable: false,
                    failureReason: selectedVersion.FailureReason ?? $"No version matched '{request.VersionRange}'.",
                    selectedFeed: candidate.Name,
                    enumeratedVersionCount: selectedVersion.EnumeratedCount,
                    cacheHit: selectedVersion.CacheHit), seenFailureKeys);
                _decisions[request.Id] = lastFailure;

                if (ShouldStopAfterCandidateFailure(request))
                {
                    break;
                }

                continue;
            }

            string installPath;
            try
            {
                installPath = await ResolveInstallPathAsync(
                    candidate,
                    request.Id,
                    selectedVersion.Version!,
                    selectedVersion.LocalPackageFile,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastFailure = Accumulate(lastFailure, FeedResolutionDecision.Failed(
                    request,
                    candidateNames,
                    correlationId: string.Empty,
                    decisionPath: "candidate-acquisition-failed",
                    feedUnavailable: false,
                    failureReason: ex.Message,
                    selectedFeed: candidate.Name,
                    enumeratedVersionCount: selectedVersion.EnumeratedCount,
                    cacheHit: selectedVersion.CacheHit), seenFailureKeys);
                _decisions[request.Id] = lastFailure;

                if (ShouldStopAfterCandidateFailure(request))
                {
                    throw;
                }

                continue;
            }
            var resolved = new ResolvedPackage(
                request.Id,
                selectedVersion.Version!,
                candidate.Name,
                installPath,
                DateTimeOffset.UtcNow,
                request.SourceName);

            _decisions[request.Id] = FeedResolutionDecision.Resolved(
                request,
                candidateNames,
                resolved,
                correlationId: string.Empty,
                decisionPath: selectedVersion.DecisionPath,
                enumeratedVersionCount: selectedVersion.EnumeratedCount,
                cacheHit: selectedVersion.CacheHit);

            return resolved;
        }

        if (lastFailure is not null)
        {
            _decisions[request.Id] = lastFailure;
            throw new NoEligibleFeedException(request.Id, lastFailure.FailureReason ?? "No candidate feed matched the requested version.");
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
    /// For local directory feeds, candidate versions are read from the nupkg files present in the
    /// directory and the match is carried forward so acquisition does not have to locate it again.
    /// For remote feeds, version enumeration and range evaluation are used.
    /// </summary>
    private async Task<VersionSelection> ResolveVersionAsync(
        FeedDefinition feed,
        PackageRequest request,
        CancellationToken cancellationToken)
    {
        var versionRequest = NuGetVersionRequestClassifier.Classify(request.VersionRange);

        // Local directory feeds are backed by nupkg files already on disk, so candidate versions
        // come from the directory itself rather than from a remote version enumeration.
        if (IsLocalDirectoryFeed(feed))
        {
            if (string.IsNullOrWhiteSpace(request.VersionRange))
            {
                return VersionSelection.Failed(
                    "local-directory-explicit-version-required",
                    $"Local directory feed '{feed.Name}' requires an explicit version for package '{request.Id}'; empty or whitespace version ranges are not supported.");
            }

            var feedDirectoryPath = feed.ServiceIndex.LocalPath;
            var packageFiles = LocalDirectoryFeedIndex.FindPackageFiles(feedDirectoryPath, request.Id);

            if (versionRequest.IsExact)
            {
                var exactMatch = LocalDirectoryFeedIndex.SelectVersion(packageFiles, versionRequest.ExactVersion!);
                if (exactMatch is null)
                {
                    return VersionSelection.Failed(
                        "exact-local-cache-miss",
                        $"Exact package '{request.Id}' version '{versionRequest.ExactVersion}' was not found in local directory feed '{feed.Name}' at '{feedDirectoryPath}'.",
                        packageFiles.Count);
                }

                return VersionSelection.Succeeded(
                    exactMatch.Value.Version.ToNormalizedString(),
                    packageFiles.Count,
                    cacheHit: true,
                    "exact-local-cache-hit",
                    exactMatch);
            }

            var localVersions = packageFiles.Select(static file => file.Version.ToNormalizedString()).ToArray();
            var localResult = IsDependencyRequest(request)
                ? SelectLowestDependencyMatch(request.VersionRange, localVersions)
                : _versionRangeEvaluator.SelectBestMatch(request.VersionRange, localVersions);

            if (!localResult.Success)
            {
                return VersionSelection.Failed(
                    "local-directory-version-no-match",
                    $"No version of package '{request.Id}' matching '{request.VersionRange}' was found in local directory feed '{feed.Name}' at '{feedDirectoryPath}': {localResult.FailureReason}",
                    localResult.CandidateCount);
            }

            return VersionSelection.Succeeded(
                localResult.SelectedVersion!,
                localResult.CandidateCount,
                cacheHit: true,
                "local-directory-version-match",
                LocalDirectoryFeedIndex.SelectVersion(packageFiles, localResult.SelectedVersion!));
        }

        if (versionRequest.IsExact)
        {
            return VersionSelection.Succeeded(versionRequest.ExactVersion!, 0, cacheHit: false, "exact-remote-direct-acquire");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var versionList = await _versionEnumerator.EnumerateVersionsAsync(feed, request.Id, cancellationToken);
            var result = IsDependencyRequest(request)
                ? SelectLowestDependencyMatch(request.VersionRange, versionList.Versions)
                : _versionRangeEvaluator.SelectBestMatch(request.VersionRange, versionList.Versions);
            stopwatch.Stop();

            _logger.LogDebug(
                "Version resolution for {PackageId} on feed {FeedName}: range={VersionRange}, selected={SelectedVersion}, candidates={CandidateCount}, cacheHit={CacheHit}, durationMs={DurationMs}",
                request.Id,
                feed.Name,
                request.VersionRange,
                result.SelectedVersion ?? "(none)",
                result.CandidateCount,
                versionList.CacheHit,
                stopwatch.ElapsedMilliseconds);

            if (!result.Success)
            {
                _metrics?.RecordVersionResolution(feed.Name, "no-match", versionList.CacheHit, stopwatch.Elapsed);
                _logger.LogDebug(
                    "No matching version found for {PackageId} on feed {FeedName}: {FailureReason}; candidates={CandidateCount}, cacheHit={CacheHit}, durationMs={DurationMs}",
                    request.Id,
                    feed.Name,
                    result.FailureReason,
                    result.CandidateCount,
                    versionList.CacheHit,
                    stopwatch.ElapsedMilliseconds);
                return VersionSelection.Failed(
                    "version-range-no-match",
                    result.FailureReason,
                    result.CandidateCount,
                    versionList.CacheHit);
            }

            _metrics?.RecordVersionResolution(feed.Name, "resolved", versionList.CacheHit, stopwatch.Elapsed);
            return VersionSelection.Succeeded(result.SelectedVersion!, result.CandidateCount, versionList.CacheHit);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            _metrics?.RecordVersionResolution(feed.Name, "cancelled", cacheHit: false, stopwatch.Elapsed);
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics?.RecordVersionResolution(feed.Name, "error", cacheHit: false, stopwatch.Elapsed);
            _logger.LogError(
                ex,
                "Version resolution failed with an exception for {PackageId} on feed {FeedName}; durationMs={DurationMs}",
                request.Id,
                feed.Name,
                stopwatch.ElapsedMilliseconds);
            return VersionSelection.Failed(
                "version-enumeration-error",
                $"Failed to enumerate or evaluate versions for package '{request.Id}' on feed '{feed.Name}': {ex.Message}");
        }
    }

    internal readonly record struct VersionSelection(
        bool Success,
        string? Version,
        int EnumeratedCount,
        bool CacheHit,
        string? FailureReason,
        string DecisionPath,
        LocalDirectoryPackageFile? LocalPackageFile = null)
    {
        public static VersionSelection Succeeded(
            string version,
            int enumeratedCount,
            bool cacheHit,
            string decisionPath = "ordered-candidate-success",
            LocalDirectoryPackageFile? localPackageFile = null) =>
            new(true, version, enumeratedCount, cacheHit, null, decisionPath, localPackageFile);

        public static VersionSelection Failed(string decisionPath, string? failureReason, int enumeratedCount = 0, bool cacheHit = false) =>
            new(false, null, enumeratedCount, cacheHit, failureReason, decisionPath);
    }

    /// <summary>
    /// Resolves the install path for a package from the specified feed.
    /// Local directory feeds extract the <c>.nupkg</c> into a stable install directory;
    /// remote feeds are downloaded and extracted via the configured remote package acquirer.
    /// </summary>
    /// <param name="feed">The feed the package was resolved from.</param>
    /// <param name="packageId">The requested package identifier.</param>
    /// <param name="version">The resolved concrete version.</param>
    /// <param name="localPackageFile">The package file already located during version selection, when the feed is a local directory.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    private async Task<string> ResolveInstallPathAsync(
        FeedDefinition feed,
        string packageId,
        string version,
        LocalDirectoryPackageFile? localPackageFile,
        CancellationToken cancellationToken)
    {
        if (!IsLocalDirectoryFeed(feed))
        {
            return await _remotePackageAcquirer.AcquireAsync(feed, packageId, version, cancellationToken);
        }

        var feedDirectoryPath = feed.ServiceIndex.LocalPath;
        var packageFile = localPackageFile
            ?? LocalDirectoryFeedIndex.FindPackageFile(feedDirectoryPath, packageId, version);

        if (packageFile is null)
        {
            var nupkgFileName = $"{packageId}.{version}.nupkg";
            throw new FileNotFoundException(
                $"Expected nupkg '{nupkgFileName}' was not found in local directory feed '{feed.Name}' at '{feedDirectoryPath}'.",
                Path.Combine(feedDirectoryPath, nupkgFileName));
        }

        var nupkgPath = packageFile.Value.FilePath;

        // Key the install directory off the identifier as spelled on disk so that requests for the
        // same package under different casing share one extraction.
        var installDir = Path.Combine(feedDirectoryPath, ".installed", packageFile.Value.PackageId, version);
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

    private bool IsRemoteFeedDisabled(
        FeedDefinition feed,
        PackageRequest request,
        IReadOnlyList<string> candidateNames,
        out FeedResolutionDecision decision)
    {
        decision = null!;
        if (IsLocalDirectoryFeed(feed))
        {
            return false;
        }

        var decisionPath = _options.OfflineMode
            ? "remote-disabled-by-offline-mode"
            : _options.RemoteFallbackMode == RemoteFallbackMode.Never
                ? "remote-disabled-by-policy"
                : null;

        if (decisionPath is null)
        {
            return false;
        }

        decision = FeedResolutionDecision.Failed(
            request,
            candidateNames,
            correlationId: string.Empty,
            decisionPath: decisionPath,
            feedUnavailable: false,
            failureReason: _options.OfflineMode
                ? "Offline mode is enabled; remote feeds are not contacted."
                : "Remote fallback mode is Never; remote feeds are not contacted.",
            selectedFeed: feed.Name);

        return true;
    }

    /// <summary>
    /// Determines whether a failed candidate ends resolution instead of falling through to the next
    /// feed. Strict mode and <see cref="FeedResolutionOptions.StopOnFirstSuccessfulFeed"/> both forbid
    /// fallback, and an explicitly requested feed has no other candidate to fall back to.
    /// </summary>
    private bool ShouldStopAfterCandidateFailure(PackageRequest request) =>
        _options.PolicyMode == FeedResolutionPolicyMode.Strict
        || _options.StopOnFirstSuccessfulFeed
        || !string.IsNullOrWhiteSpace(request.FeedName);

    /// <summary>
    /// Folds a candidate failure into the running failure so the final diagnostic names every feed
    /// that was tried, instead of only the one that happened to be tried last.
    /// Deduplication is keyed by <c>(DecisionPath, FailureReason)</c> so that identical policy-wide
    /// diagnostics (e.g. offline-mode applied to every remote feed) are collapsed into one entry
    /// while feed-specific failures that share a decision path but differ in reason are all preserved.
    /// </summary>
    private static FeedResolutionDecision Accumulate(
        FeedResolutionDecision? previous,
        FeedResolutionDecision current,
        HashSet<string> seenKeys)
    {
        var key = $"{current.DecisionPath}\x1f{current.FailureReason}";
        if (!seenKeys.Add(key))
        {
            return previous!;
        }

        return previous is null ? current : CombineFailureDiagnostics(previous, current);
    }

    private static FeedResolutionDecision CombineFailureDiagnostics(
        FeedResolutionDecision previous,
        FeedResolutionDecision current)
    {
        return current with
        {
            DecisionPath = $"{previous.DecisionPath}+{current.DecisionPath}",
            FailureReason = $"{previous.FailureReason} {current.FailureReason}"
        };
    }

    private static bool IsDependencyRequest(PackageRequest request) =>
        request.SourceName.StartsWith("dependency-of:", StringComparison.OrdinalIgnoreCase);

    private static VersionResolutionResult SelectLowestDependencyMatch(string versionRange, IReadOnlyList<string> availableVersions)
    {
        var parsedVersions = new List<(string OriginalVersion, NuGetVersion ParsedVersion)>();
        foreach (var version in availableVersions)
        {
            if (NuGetVersion.TryParse(version, out var parsedVersion))
            {
                parsedVersions.Add((version, parsedVersion));
            }
        }

        if (parsedVersions.Count == 0)
        {
            return new(false, null, availableVersions.Count, "Resolution failed: no versions available.");
        }

        if (!VersionRange.TryParse(versionRange, out var range))
        {
            return new(false, null, availableVersions.Count, $"Resolution failed: invalid version range '{versionRange}'.");
        }

        var selectedVersion = parsedVersions
            .Where(candidate => range.Satisfies(candidate.ParsedVersion))
            .OrderBy(candidate => candidate.ParsedVersion)
            .FirstOrDefault();

        return string.IsNullOrEmpty(selectedVersion.OriginalVersion)
            ? new(false, null, availableVersions.Count, $"Resolution failed: no version matched range '{versionRange}'.")
            : new(true, selectedVersion.OriginalVersion, availableVersions.Count, null);
    }

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
