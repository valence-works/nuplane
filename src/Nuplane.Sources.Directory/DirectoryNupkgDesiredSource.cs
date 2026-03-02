using System.Text.RegularExpressions;
using Nuplane.Abstractions;

namespace Nuplane.Sources.Directory;

public sealed class DirectoryNupkgDesiredSource : IDesiredPackageSource
{
    private static readonly Regex PackageFileNamePattern = new(
        "^(?<id>.+)\\.(?<version>\\d+\\.\\d+\\.\\d+(?:[-+][A-Za-z0-9\\.-]+)?)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string sourceName;
    private readonly string directoryPath;
    private readonly HashSet<string> allowlistedPackageIds;

    public DirectoryNupkgDesiredSource(string sourceName, string directoryPath, IEnumerable<string>? allowlistedPackageIds = null)
    {
        this.sourceName = string.IsNullOrWhiteSpace(sourceName) ? throw new ArgumentException("Source name is required.", nameof(sourceName)) : sourceName;
        this.directoryPath = string.IsNullOrWhiteSpace(directoryPath) ? throw new ArgumentException("Directory path is required.", nameof(directoryPath)) : directoryPath;
        this.allowlistedPackageIds = allowlistedPackageIds is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(allowlistedPackageIds, StringComparer.OrdinalIgnoreCase);
    }

    public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!System.IO.Directory.Exists(directoryPath))
        {
            return Task.FromResult<IReadOnlyList<PackageRequest>>(Array.Empty<PackageRequest>());
        }

        var requests = System.IO.Directory
            .EnumerateFiles(directoryPath, "*.nupkg", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
            .Select(fileName => CreateRequest(fileName!))
            .Where(request => request is not null)
            .Cast<PackageRequest>()
            .OrderBy(request => request.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(request => request.VersionRange, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult<IReadOnlyList<PackageRequest>>(requests);
    }

    private PackageRequest? CreateRequest(string fileNameWithoutExtension)
    {
        var match = PackageFileNamePattern.Match(fileNameWithoutExtension);
        if (!match.Success)
        {
            return null;
        }

        var packageId = match.Groups["id"].Value;
        if (allowlistedPackageIds.Count > 0 && !allowlistedPackageIds.Contains(packageId))
        {
            return null;
        }

        var version = match.Groups["version"].Value;
        return new(packageId, version, null, PackageUpdatePolicy.Exact, sourceName);
    }
}