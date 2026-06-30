using System.IO.Compression;

namespace Nuplane;

/// <summary>
/// Reads files shipped inside an installed package's content, transparently handling both extracted install
/// directories and <c>.nupkg</c> archives. Hosts that need to inspect package-shipped files — manifests,
/// nuspecs, and similar — should use this rather than re-deriving the on-disk package layout themselves.
/// </summary>
public static class PackageContent
{
    /// <summary>
    /// Reads the bytes of a package-relative file (for example <c>"build/my-manifest.json"</c>) from the package
    /// installed at <paramref name="installPath"/>.
    /// </summary>
    /// <param name="installPath">The package install path — either an extracted directory or a <c>.nupkg</c> file.</param>
    /// <param name="relativePath">The forward-slash or backslash separated path of the file within the package.</param>
    /// <returns>The file bytes, or <see langword="null"/> when the file does not exist or the content cannot be read.</returns>
    public static byte[]? TryReadFile(string installPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || string.IsNullOrWhiteSpace(relativePath))
            return null;

        try
        {
            if (Directory.Exists(installPath))
            {
                var path = Path.Combine(installPath, ToOsRelativePath(relativePath));
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }

            if (IsNupkg(installPath) && File.Exists(installPath))
                return ReadArchiveEntry(installPath, entry => PathsEqual(entry, relativePath));

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the first file at the package content root whose name ends with <paramref name="extension"/>
    /// (case-insensitive, for example <c>".nuspec"</c>) and returns its name and bytes.
    /// </summary>
    /// <param name="installPath">The package install path — either an extracted directory or a <c>.nupkg</c> file.</param>
    /// <param name="extension">The file extension to match, including the leading dot.</param>
    /// <returns>The matching file, or <see langword="null"/> when none exists or the content cannot be read.</returns>
    public static PackageContentFile? TryFindByExtension(string installPath, string extension)
    {
        if (string.IsNullOrWhiteSpace(installPath) || string.IsNullOrWhiteSpace(extension))
            return null;

        try
        {
            if (Directory.Exists(installPath))
            {
                var match = Directory
                    .EnumerateFiles(installPath, "*" + extension, SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();

                return match is null ? null : new PackageContentFile(Path.GetFileName(match), File.ReadAllBytes(match));
            }

            if (IsNupkg(installPath) && File.Exists(installPath))
            {
                using var stream = File.OpenRead(installPath);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                var entry = archive.Entries.FirstOrDefault(x =>
                    IsRootEntry(x.FullName) &&
                    x.FullName.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

                return entry is null ? null : new PackageContentFile(entry.Name, ReadEntry(entry));
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    private static byte[]? ReadArchiveEntry(string archivePath, Func<string, bool> matches)
    {
        using var stream = File.OpenRead(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(x => matches(x.FullName));
        return entry is null ? null : ReadEntry(entry);
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var entryStream = entry.Open();
        using var memory = new MemoryStream();
        entryStream.CopyTo(memory);
        return memory.ToArray();
    }

    private static bool IsNupkg(string path) =>
        string.Equals(Path.GetExtension(path), ".nupkg", StringComparison.OrdinalIgnoreCase);

    private static string ToOsRelativePath(string relativePath) =>
        Normalize(relativePath).Replace('/', Path.DirectorySeparatorChar);

    private static bool IsRootEntry(string entryFullName) => !Normalize(entryFullName).Contains('/');

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}

/// <summary>A file read from a package's content: its name and raw bytes.</summary>
/// <param name="Name">The file name (without directory).</param>
/// <param name="Content">The file bytes.</param>
public sealed record PackageContentFile(string Name, byte[] Content);
