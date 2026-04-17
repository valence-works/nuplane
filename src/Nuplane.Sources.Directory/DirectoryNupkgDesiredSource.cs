using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;
using Nuplane.Abstractions;

namespace Nuplane.Sources.Directory;

/// <summary>
/// A desired-state source that discovers NuGet packages from <c>.nupkg</c> files in a
/// directory, optionally filtering by a set of package identifier patterns.
/// Patterns support <c>*</c> (any sequence of characters) and <c>?</c> (any single character).
/// </summary>
/// <param name="sourceName">A descriptive name for this desired-state source.</param>
/// <param name="directoryPath">The directory to scan for <c>.nupkg</c> files.</param>
/// <param name="allowlistedPackageIds">An optional set of package identifier patterns. If <see langword="null"/> or empty, no packages are included.</param>
/// <param name="logger">An optional logger for diagnostic output.</param>
/// <param name="feedName">The optional local directory feed name to set on produced package requests.</param>
/// <param name="stabilityProbe">An optional stability probe for partial-write safety. When provided, each discovered <c>.nupkg</c> file is probed for stability before being included.</param>
public sealed class DirectoryNupkgDesiredSource(string sourceName, string directoryPath, IEnumerable<string>? allowlistedPackageIds = null, ILogger<DirectoryNupkgDesiredSource>? logger = null, string? feedName = null, NupkgFileStabilityProbe? stabilityProbe = null) : IDesiredPackageSource
{
    private static readonly Regex PackageFileNamePattern = new(
        "^(?<id>.+)\\.(?<version>\\d+\\.\\d+\\.\\d+(?:[-+][A-Za-z0-9\\.-]+)?)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _sourceName = string.IsNullOrWhiteSpace(sourceName) ? throw new ArgumentException("Source name is required.", nameof(sourceName)) : sourceName;
    private readonly string _directoryPath = string.IsNullOrWhiteSpace(directoryPath) ? throw new ArgumentException("Directory path is required.", nameof(directoryPath)) : directoryPath;
    private readonly string? _feedName = string.IsNullOrWhiteSpace(feedName) ? null : feedName;
    private readonly IReadOnlyList<string> _includePatterns = allowlistedPackageIds is null
        ? []
        : allowlistedPackageIds.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
    private readonly ILogger<DirectoryNupkgDesiredSource> _logger = logger ?? NullLogger<DirectoryNupkgDesiredSource>.Instance;

    /// <inheritdoc />
    public async Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!System.IO.Directory.Exists(_directoryPath))
        {
            return [];
        }

        string[] filePaths;
        try
        {
            filePaths = System.IO.Directory
                .EnumerateFiles(_directoryPath, "*.nupkg", SearchOption.TopDirectoryOnly)
                .ToArray();
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate .nupkg files in '{DirectoryPath}'. Returning empty desired state.", _directoryPath);
            return Array.Empty<PackageRequest>();
        }

        var requests = new List<PackageRequest>();
        foreach (var filePath in filePaths)
        {
            ct.ThrowIfCancellationRequested();

            if (stabilityProbe is not null)
            {
                var isStable = await stabilityProbe.IsStableAsync(filePath, ct);
                if (!isStable)
                {
                    _logger.LogDebug(
                        "Skipping unstable file '{FilePath}' — it may still be in the process of being written.",
                        filePath);
                    continue;
                }
            }

            var fileName = Path.GetFileNameWithoutExtension(filePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            var request = CreateRequest(fileName);
            if (request is not null)
            {
                requests.Add(request);
            }
        }

        // When multiple versions of the same package exist in the directory,
        // keep only the highest version. The downstream aggregator deduplicates by
        // package ID using alphabetical tie-breaking on VersionRange, which would
        // incorrectly select the lowest version (e.g., "1.0.0" before "1.0.1").
        var dedupedByHighestVersion = requests
            .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() == 1
                ? g.First()
                : g.OrderByDescending(r => NuGetVersion.Parse(r.VersionRange)).First())
            .OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.VersionRange, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return dedupedByHighestVersion;
    }

    private PackageRequest? CreateRequest(string fileNameWithoutExtension)
    {
        var match = PackageFileNamePattern.Match(fileNameWithoutExtension);
        if (!match.Success)
        {
            return null;
        }

        var packageId = match.Groups["id"].Value;
        if (_includePatterns.Count == 0 || !PackagePatternMatcher.MatchesAny(_includePatterns, packageId))
        {
            return null;
        }

        var version = match.Groups["version"].Value;
        return new(packageId, version, _feedName, PackageUpdatePolicy.Exact, _sourceName);
    }
}