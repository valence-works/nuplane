using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Sources.Directory;

/// <summary>
/// A desired-state source that discovers NuGet packages from <c>.nupkg</c> files in a
/// directory, optionally filtering by a set of package identifier patterns.
/// Patterns support <c>*</c> (any sequence of characters) and <c>?</c> (any single character).
/// </summary>
public sealed class DirectoryNupkgDesiredSource : IScopedDesiredPackageSource
{
    private static readonly Regex PackageFileNamePattern = new(
        "^(?<id>.+)\\.(?<version>\\d+\\.\\d+\\.\\d+(?:[-+][A-Za-z0-9\\.-]+)?)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _sourceName;
    private readonly string _directoryPath;
    private readonly string? _feedName;
    private readonly IReadOnlyList<string> _includePatterns;
    private readonly ILogger<DirectoryNupkgDesiredSource> _logger;
    private readonly NupkgFileStabilityProbe? _stabilityProbe;
    private readonly bool _requiresBorrow;

    /// <summary>Creates an ordinary directory-backed desired-state source.</summary>
    /// <param name="sourceName">A descriptive name for this desired-state source.</param>
    /// <param name="directoryPath">The directory to scan for <c>.nupkg</c> files.</param>
    /// <param name="allowlistedPackageIds">An optional set of package identifier patterns. If <see langword="null"/> or empty, no packages are included.</param>
    /// <param name="logger">An optional logger for diagnostic output.</param>
    /// <param name="feedName">The optional local directory feed name to set on produced package requests.</param>
    /// <param name="stabilityProbe">An optional stability probe for partial-write safety. When provided, each discovered <c>.nupkg</c> file is probed for stability before being included.</param>
    public DirectoryNupkgDesiredSource(
        string sourceName,
        string directoryPath,
        IEnumerable<string>? allowlistedPackageIds = null,
        ILogger<DirectoryNupkgDesiredSource>? logger = null,
        string? feedName = null,
        NupkgFileStabilityProbe? stabilityProbe = null)
    {
        _sourceName = string.IsNullOrWhiteSpace(sourceName)
            ? throw new ArgumentException("Source name is required.", nameof(sourceName))
            : sourceName;
        _directoryPath = string.IsNullOrWhiteSpace(directoryPath)
            ? throw new ArgumentException("Directory path is required.", nameof(directoryPath))
            : directoryPath;
        _feedName = string.IsNullOrWhiteSpace(feedName) ? null : feedName;
        _includePatterns = allowlistedPackageIds is null
            ? []
            : allowlistedPackageIds.Where(static pattern => !string.IsNullOrWhiteSpace(pattern)).ToArray();
        _logger = logger ?? NullLogger<DirectoryNupkgDesiredSource>.Instance;
        _stabilityProbe = stabilityProbe;
    }

    internal DirectoryNupkgDesiredSource(
        string sourceName,
        string directoryPath,
        IEnumerable<string>? allowlistedPackageIds,
        ILogger<DirectoryNupkgDesiredSource>? logger,
        string? feedName,
        NupkgFileStabilityProbe? stabilityProbe,
        bool requiresBorrow)
        : this(sourceName, directoryPath, allowlistedPackageIds, logger, feedName, stabilityProbe)
    {
        _requiresBorrow = requiresBorrow;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
    {
        if (_requiresBorrow)
            throw new PackageStoreAdmissionException(
                PackageStoreAdmissionReason.UnsupportedParticipant,
                "This directory desired source is a coordinated participant and requires the current operation borrow.");

        return ReadOrdinaryAsync(ct);
    }

    internal Task<IReadOnlyList<PackageRequest>> GetDesiredGuardedAsync(CancellationToken ct)
        => ReadDesiredNativeAsync(borrow: null, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(PackageStoreOperationBorrow borrow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        return ReadDesiredNativeAsync(borrow, ct);
    }

    private async Task<IReadOnlyList<PackageRequest>> ReadOrdinaryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!System.IO.Directory.Exists(_directoryPath))
            return Array.Empty<PackageRequest>();

        string[] filePaths;
        try
        {
            filePaths = System.IO.Directory
                .EnumerateFiles(_directoryPath, "*.nupkg", SearchOption.TopDirectoryOnly)
                .ToArray();
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception,
                "Failed to enumerate .nupkg files in '{DirectoryPath}'. Returning empty desired state.",
                _directoryPath);
            return Array.Empty<PackageRequest>();
        }

        var stablePaths = _stabilityProbe is null
            ? filePaths
            : await _stabilityProbe.GetStableFilesAsync(filePaths, cancellationToken).ConfigureAwait(false);
        return CreateRequests(stablePaths, cancellationToken);
    }

    private Task<IReadOnlyList<PackageRequest>> ReadDesiredNativeAsync(
        PackageStoreOperationBorrow? borrow,
        CancellationToken cancellationToken)
        => DesiredSourceDirectoryAccess.WithDirectoryAsync<IReadOnlyList<PackageRequest>>(
            _directoryPath,
            borrow,
            async session =>
            {
                if (session.IsMissing)
                    return Array.Empty<PackageRequest>();

                IReadOnlyList<string> candidates;
                try
                {
                    candidates = session.EnumeratePackageCandidates();
                }
                catch (IOException exception)
                {
                    _logger.LogWarning(exception,
                        "Failed to enumerate .nupkg files in '{DirectoryPath}'. Returning empty desired state.",
                        _directoryPath);
                    return Array.Empty<PackageRequest>();
                }

                if (_stabilityProbe is not null)
                {
                    var stableCandidates = await _stabilityProbe.GetStableFilesAsync(
                            session, _directoryPath,
                            candidates.Where(static candidate => !string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(candidate))),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return CreateRequests(stableCandidates, cancellationToken);
                }

                var verifiedCandidates = new List<string>();
                foreach (var candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(candidate)))
                        continue;

                    try
                    {
                        var sample = session.SamplePackageCandidate(candidate);
                        if (sample.Kind == PhysicalStoreDirectoryCandidateKind.Busy)
                        {
                            // Without a probe, include busy files only when native metadata has
                            // positively verified a regular single-link file.
                            _logger.LogDebug(
                                "Including metadata-verified package candidate '{FilePath}' without a stability probe.",
                                Path.Combine(_directoryPath, candidate));
                        }
                        else if (sample.Kind != PhysicalStoreDirectoryCandidateKind.RegularFile)
                        {
                            continue;
                        }

                        verifiedCandidates.Add(candidate);
                    }
                    finally
                    {
                        session.ReleasePackageCandidate(candidate);
                    }
                }

                return CreateRequests(verifiedCandidates, cancellationToken);
            },
            cancellationToken);

    private IReadOnlyList<PackageRequest> CreateRequests(
        IEnumerable<string> candidates, CancellationToken cancellationToken)
    {
        var requests = new List<PackageRequest>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileNameWithoutExtension(candidate);
            if (string.IsNullOrWhiteSpace(fileName))
                continue;

            var request = CreateRequest(fileName);
            if (request is not null)
                requests.Add(request);
        }

        return DeduplicateByHighestVersion(requests);
    }

    private static IReadOnlyList<PackageRequest> DeduplicateByHighestVersion(List<PackageRequest> requests) => requests
        .GroupBy(request => request.Id, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.Count() == 1
            ? group.First()
            : group.OrderByDescending(request => NuGetVersion.Parse(request.VersionRange)).First())
        .OrderBy(request => request.Id, StringComparer.OrdinalIgnoreCase)
        .ThenBy(request => request.VersionRange, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private PackageRequest? CreateRequest(string fileNameWithoutExtension)
    {
        var match = PackageFileNamePattern.Match(fileNameWithoutExtension);
        if (!match.Success)
            return null;

        var packageId = match.Groups["id"].Value;
        if (_includePatterns.Count == 0 || !PackagePatternMatcher.MatchesAny(_includePatterns, packageId))
            return null;

        var version = match.Groups["version"].Value;
        return new PackageRequest(packageId, version, _feedName, PackageUpdatePolicy.Exact, _sourceName);
    }
}
