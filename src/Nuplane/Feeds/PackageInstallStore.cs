using System.IO.Compression;
using Nuplane.Feeds.Configuration;

namespace Nuplane.Feeds;

/// <summary>
/// Owns the on-disk layout of extracted packages and the rules for publishing an extraction into it.
/// Every feed kind — local directory and remote alike — extracts under the single writable root
/// derived from <see cref="FeedResolutionOptions.PackageInstallRoot"/>, so a feed directory that only
/// supplies <c>.nupkg</c> files never has to be writable and can be mounted read-only.
/// An install directory only counts as usable once it carries the completion marker, so a partially
/// extracted directory is never handed to the loader.
/// </summary>
internal static class PackageInstallStore
{
    /// <summary>
    /// The marker file written as the last step of an extraction, inside the staging directory and
    /// therefore already present when the directory is published under its final name.
    /// </summary>
    public const string CompletionMarkerFileName = ".nuplane-ready";

    /// <summary>
    /// Resolves the writable root that all package extractions land under.
    /// </summary>
    /// <param name="options">The feed resolution options carrying the configured root.</param>
    public static string ResolveInstallRoot(FeedResolutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return !string.IsNullOrWhiteSpace(options.PackageInstallRoot)
            ? Path.GetFullPath(options.PackageInstallRoot)
            : Path.Combine(AppContext.BaseDirectory, ".nuplane", "packages");
    }

    /// <summary>
    /// Composes the install directory for a concrete package version acquired from a feed.
    /// </summary>
    /// <param name="installRoot">The writable install root.</param>
    /// <param name="feedName">The name of the feed the package came from.</param>
    /// <param name="packageId">The package identifier to key the directory off.</param>
    /// <param name="version">The concrete package version.</param>
    public static string GetInstallDirectory(string installRoot, string feedName, string packageId, string version) =>
        Path.Combine(
            installRoot,
            SanitizePathSegment(feedName),
            SanitizePathSegment(packageId),
            SanitizePathSegment(version));

    /// <summary>
    /// Determines whether the install directory holds a completed extraction.
    /// </summary>
    /// <param name="installDirectory">The install directory to probe.</param>
    public static bool IsInstalled(string installDirectory) =>
        File.Exists(Path.Combine(installDirectory, CompletionMarkerFileName));

    /// <summary>
    /// Creates the staging area and returns a unique path inside it; the path itself is not created.
    /// Staging sits under the install root so that publishing an extraction is a rename within one
    /// volume.
    /// </summary>
    /// <param name="installRoot">The writable install root.</param>
    /// <param name="extension">An optional file extension for staged files.</param>
    public static string CreateStagingPath(string installRoot, string extension = "")
    {
        var stagingRoot = Path.Combine(installRoot, ".tmp");
        Directory.CreateDirectory(stagingRoot);
        return Path.Combine(stagingRoot, $"{Guid.NewGuid():N}{extension}");
    }

    /// <summary>
    /// Extracts a <c>.nupkg</c> into <paramref name="installDirectory"/>, staging the extraction under
    /// the install root and publishing it with a single rename so no half-extracted directory is ever
    /// reachable under the final path. The source <c>.nupkg</c> is only read.
    /// </summary>
    /// <param name="installRoot">The writable install root.</param>
    /// <param name="installDirectory">The final install directory, below <paramref name="installRoot"/>.</param>
    /// <param name="nupkgPath">The package file to extract.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public static async Task InstallAsync(
        string installRoot,
        string installDirectory,
        string nupkgPath,
        CancellationToken cancellationToken)
    {
        var stagingDirectory = CreateStagingPath(installRoot);

        try
        {
            Directory.CreateDirectory(stagingDirectory);
            ZipFile.ExtractToDirectory(nupkgPath, stagingDirectory, overwriteFiles: true);
            await File.WriteAllTextAsync(
                Path.Combine(stagingDirectory, CompletionMarkerFileName),
                string.Empty,
                cancellationToken);

            Directory.CreateDirectory(Path.GetDirectoryName(installDirectory)!);

            if (TryPublish(stagingDirectory, installDirectory))
            {
                stagingDirectory = string.Empty;
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(stagingDirectory) && Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Renames a completed staging directory onto the final install path.
    /// Returns <see langword="false"/> when a concurrent writer already published a complete extraction
    /// there, in which case the staged copy is redundant and the caller discards it. A directory left
    /// behind by an interrupted extraction is replaced instead, because it carries no marker and is
    /// therefore not in use.
    /// </summary>
    private static bool TryPublish(string stagingDirectory, string installDirectory)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Directory.Move(stagingDirectory, installDirectory);
                return true;
            }
            catch (IOException) when (Directory.Exists(installDirectory))
            {
                if (IsInstalled(installDirectory))
                {
                    return false;
                }

                if (attempt > 0)
                {
                    throw;
                }

                try
                {
                    Directory.Delete(installDirectory, recursive: true);
                }
                catch (DirectoryNotFoundException)
                {
                    // A concurrent writer removed it first; the retried rename is what matters.
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Reduces a value to a single safe path segment. Besides characters the platform rejects, this
    /// also neutralizes directory separators and relative segments so that a hostile package
    /// identifier, feed name, or version cannot escape the install root.
    /// </summary>
    private static string SanitizePathSegment(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var buffer = value
            .Select(ch => invalidCharacters.Contains(ch)
                || ch == Path.DirectorySeparatorChar
                || ch == Path.AltDirectorySeparatorChar
                    ? '_'
                    : ch)
            .ToArray();
        var sanitized = new string(buffer);

        return sanitized is "" or "." or ".." ? "_" : sanitized;
    }
}
