using NuGet.Versioning;

namespace Nuplane.Feeds;

/// <summary>
/// A <c>.nupkg</c> file discovered inside a local directory feed.
/// </summary>
/// <param name="FilePath">The full path of the package file.</param>
/// <param name="PackageId">The package identifier exactly as it is spelled in the file name.</param>
/// <param name="Version">The parsed package version.</param>
internal readonly record struct LocalDirectoryPackageFile(string FilePath, string PackageId, NuGetVersion Version);

/// <summary>
/// Locates <c>.nupkg</c> files inside a local directory feed.
/// Package identifiers are matched case-insensitively and versions by their normalized form, because
/// NuGet writes restored package files in lower case while nuspec dependencies declare canonical ids.
/// Composing a file name from the requested identifier would therefore miss packages the directory
/// demonstrably contains whenever the host runs on a case-sensitive file system, such as inside a
/// Linux container.
/// </summary>
internal static class LocalDirectoryFeedIndex
{
    private static readonly EnumerationOptions NupkgEnumerationOptions = new()
    {
        MatchCasing = MatchCasing.CaseInsensitive,
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
    };

    /// <summary>
    /// Returns every package file in the directory that carries the requested package identifier,
    /// ordered by version and then by path so selection is deterministic.
    /// </summary>
    /// <param name="directoryPath">The local directory feed path.</param>
    /// <param name="packageId">The requested package identifier, in any casing.</param>
    public static IReadOnlyList<LocalDirectoryPackageFile> FindPackageFiles(string directoryPath, string packageId)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)
            || string.IsNullOrWhiteSpace(packageId)
            || !Directory.Exists(directoryPath))
        {
            return [];
        }

        var matches = new List<LocalDirectoryPackageFile>();

        foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*.nupkg", NupkgEnumerationOptions))
        {
            var fileName = Path.GetFileName(filePath);
            if (!TryParsePackageFileName(fileName, packageId, out var parsedPackageId, out var version))
            {
                continue;
            }

            matches.Add(new(filePath, parsedPackageId, version));
        }

        return matches
            .OrderBy(static match => match.Version)
            .ThenBy(static match => match.FilePath, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Returns the package file carrying the requested version, or <see langword="null"/> when the
    /// directory does not contain it.
    /// </summary>
    /// <param name="directoryPath">The local directory feed path.</param>
    /// <param name="packageId">The requested package identifier, in any casing.</param>
    /// <param name="version">The requested version, in any equivalent form.</param>
    public static LocalDirectoryPackageFile? FindPackageFile(string directoryPath, string packageId, string version) =>
        SelectVersion(FindPackageFiles(directoryPath, packageId), version);

    /// <summary>
    /// Returns the candidate carrying the requested version, comparing versions by their normalized form
    /// so that equivalent spellings such as <c>1.0</c> and <c>1.0.0</c> match.
    /// </summary>
    /// <param name="candidates">The package files to select from.</param>
    /// <param name="version">The requested version.</param>
    public static LocalDirectoryPackageFile? SelectVersion(
        IReadOnlyList<LocalDirectoryPackageFile> candidates,
        string version)
    {
        if (!NuGetVersion.TryParse(version, out var requestedVersion))
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Version.Equals(requestedVersion))
            {
                return candidate;
            }
        }

        return null;
    }

    internal static bool TryParsePackageFileName(
        string fileName,
        string packageId,
        out string parsedPackageId,
        out NuGetVersion version)
    {
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var prefix = packageId + ".";
        if (nameWithoutExtension.Length > prefix.Length
            && nameWithoutExtension.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && NuGetVersion.TryParse(nameWithoutExtension[prefix.Length..], out var parsedVersion))
        {
            parsedPackageId = nameWithoutExtension[..packageId.Length];
            version = parsedVersion;
            return true;
        }

        parsedPackageId = string.Empty;
        version = null!;
        return false;
    }
}
