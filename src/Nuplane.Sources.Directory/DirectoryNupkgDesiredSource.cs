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

    /// <summary>Creates a native, no-follow directory-backed desired-state source.</summary>
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

    /// <inheritdoc />
    public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
        => ReadDesiredAsync(borrow: null, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(PackageStoreOperationBorrow borrow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        return ReadDesiredAsync(borrow, ct);
    }

    private Task<IReadOnlyList<PackageRequest>> ReadDesiredAsync(
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

                var requests = new List<PackageRequest>();
                foreach (var candidate in candidates)
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var fileName = candidate[..^".nupkg".Length];
                        if (string.IsNullOrWhiteSpace(fileName))
                            continue;

                        if (_stabilityProbe is not null)
                        {
                            var stable = await _stabilityProbe.IsStableAsync(
                                    session, candidate, Path.Combine(_directoryPath, candidate), cancellationToken)
                                .ConfigureAwait(false);
                            if (!stable)
                            {
                                _logger.LogDebug(
                                    "Skipping unstable file '{FilePath}' — it may still be in the process of being written.",
                                    Path.Combine(_directoryPath, candidate));
                                continue;
                            }
                        }
                        else
                        {
                            var sample = session.SamplePackageCandidate(candidate);
                            if (sample.Kind == PhysicalStoreDirectoryCandidateKind.Busy)
                            {
                                // Historically the optional stability probe was the only operation
                                // that opened package contents. Without it, preserve enumeration
                                // behavior only when native metadata positively proved this exact
                                // candidate is a regular single-link file.
                                _logger.LogDebug(
                                    "Including metadata-verified package candidate '{FilePath}' without a stability probe.",
                                    Path.Combine(_directoryPath, candidate));
                            }
                            else if (sample.Kind != PhysicalStoreDirectoryCandidateKind.RegularFile)
                            {
                                continue;
                            }
                        }

                        var request = CreateRequest(fileName);
                        if (request is not null)
                            requests.Add(request);
                    }
                    finally
                    {
                        // Retain a Windows metadata pin only through this package's stability awaits;
                        // large feeds must not accumulate one native handle per matching filename.
                        session.ReleasePackageCandidate(candidate);
                    }
                }

                // Keep the highest NuGet semantic version for each package ID. Ordering is deterministic.
                return requests
                    .GroupBy(request => request.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.Count() == 1
                        ? group.First()
                        : group.OrderByDescending(request => NuGetVersion.Parse(request.VersionRange)).First())
                    .OrderBy(request => request.Id, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(request => request.VersionRange, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            },
            cancellationToken);

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
