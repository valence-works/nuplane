namespace Nuplane.Tests.Shared;

/// <summary>
/// Owns one isolated package-install root and one state-file slot for store tests.
/// </summary>
public sealed class PackageStoreFixture : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Creates a fixture with a unique temporary directory, package root, and state slot.
    /// </summary>
    public PackageStoreFixture()
    {
        RootPath = Path.Combine(Path.GetTempPath(), $"nuplane-package-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(RootPath);

        try
        {
            PackageInstallRoot = CreateDirectory("packages");
            StateFilePath = CreateStateSlot("state/store-state.json");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Gets the unique directory owned by this fixture.
    /// </summary>
    public string RootPath { get; }

    /// <summary>
    /// Gets the fixture's isolated package-install root.
    /// </summary>
    public string PackageInstallRoot { get; }

    /// <summary>
    /// Gets the fixture's default state-file slot. The file itself is not pre-created.
    /// </summary>
    public string StateFilePath { get; }

    /// <summary>
    /// Resolves a relative path inside the fixture after rejecting traversal and links.
    /// </summary>
    /// <param name="relativePath">A relative path made of ordinary path segments; repeated separators are normalized.</param>
    /// <returns>The full path inside <see cref="RootPath"/>.</returns>
    /// <exception cref="ArgumentException">The path is rooted, empty, or contains traversal segments.</exception>
    /// <exception cref="InvalidOperationException">An existing path component is a symbolic link or reparse point.</exception>
    public string GetPath(string relativePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var segments = ValidateAndSplit(relativePath);
        var fullPath = Path.GetFullPath(Path.Combine(RootPath, Path.Combine(segments)));
        var relativeToRoot = Path.GetRelativePath(RootPath, fullPath);

        if (relativeToRoot is "." or ".." ||
            relativeToRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relativeToRoot.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relativeToRoot))
        {
            throw new ArgumentException("The path must remain below this fixture's root.", nameof(relativePath));
        }

        EnsureNoLinks(relativeToRoot);
        return fullPath;
    }

    /// <summary>
    /// Creates a directory inside the fixture after validating every existing path component.
    /// </summary>
    /// <param name="relativePath">A relative directory path inside the fixture.</param>
    /// <returns>The created directory's full path.</returns>
    public string CreateDirectory(string relativePath)
    {
        var path = GetPath(relativePath);
        Directory.CreateDirectory(path);
        return GetPath(relativePath);
    }

    /// <summary>
    /// Creates the parent directory for a new state file and returns its path without creating the file.
    /// </summary>
    /// <param name="relativePath">A relative state-file path inside the fixture.</param>
    /// <returns>The state-file slot's full path.</returns>
    /// <exception cref="IOException">The requested slot already exists.</exception>
    public string CreateStateSlot(string relativePath)
    {
        var slotPath = GetPath(relativePath);
        var parentPath = Path.GetDirectoryName(slotPath)!;
        Directory.CreateDirectory(parentPath);

        // Re-resolve after creation so a pre-existing or newly observed link is never returned.
        slotPath = GetPath(relativePath);
        if (TryGetAttributes(slotPath, out _) || TryGetLinkTarget(slotPath, out _))
        {
            throw new IOException("The requested state-file slot already exists.");
        }

        return slotPath;
    }

    /// <summary>
    /// Removes only entries owned by this fixture and never traverses a link or reparse point.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        DeleteEntryWithoutFollowingLinks(RootPath);
        _disposed = true;
    }

    private static string[] ValidateAndSplit(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (Path.IsPathRooted(relativePath) ||
            relativePath[0] == '/' ||
            relativePath[0] == '\\' ||
            (relativePath.Length >= 2 && char.IsAsciiLetter(relativePath[0]) && relativePath[1] == ':'))
        {
            throw new ArgumentException("The path must be relative to this fixture.", nameof(relativePath));
        }

        var segments = relativePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("The path must not contain current-directory or parent-directory segments.", nameof(relativePath));
        }

        return segments;
    }

    private void EnsureNoLinks(string relativePath)
    {
        if (IsLinkOrReparsePoint(RootPath))
        {
            throw new InvalidOperationException("The fixture root was replaced with a link or reparse point.");
        }

        var currentPath = RootPath;
        foreach (var segment in relativePath.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (IsLinkOrReparsePoint(currentPath))
            {
                throw new InvalidOperationException($"The fixture path contains a link or reparse point: {segment}.");
            }

            if (!TryGetAttributes(currentPath, out _))
            {
                break;
            }
        }
    }

    private static bool IsLinkOrReparsePoint(string path)
    {
        if (TryGetLinkTarget(path, out _))
        {
            return true;
        }

        return TryGetAttributes(path, out var attributes) &&
               (attributes & FileAttributes.ReparsePoint) != 0;
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static bool TryGetLinkTarget(string path, out FileAttributes attributes)
    {
        attributes = default;
        if (ReadLinkTarget(new FileInfo(path)) is not null || ReadLinkTarget(new DirectoryInfo(path)) is not null)
        {
            _ = TryGetAttributes(path, out attributes);
            return true;
        }

        return false;
    }

    private static string? ReadLinkTarget(FileSystemInfo fileSystemInfo)
    {
        try
        {
            return fileSystemInfo.LinkTarget;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void DeleteEntryWithoutFollowingLinks(string path)
    {
        var isLink = TryGetLinkTarget(path, out var linkAttributes);
        if (!isLink && !TryGetAttributes(path, out linkAttributes))
        {
            return;
        }

        if (isLink || (linkAttributes & FileAttributes.ReparsePoint) != 0)
        {
            DeleteLink(path, linkAttributes);
            return;
        }

        if ((linkAttributes & FileAttributes.Directory) != 0)
        {
            foreach (var childPath in Directory.EnumerateFileSystemEntries(path))
            {
                DeleteEntryWithoutFollowingLinks(childPath);
            }

            Directory.Delete(path);
            return;
        }

        File.Delete(path);
    }

    private static void DeleteLink(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.Directory) != 0)
        {
            Directory.Delete(path);
            return;
        }

        try
        {
            Directory.Delete(path);
        }
        catch (IOException)
        {
            File.Delete(path);
        }
    }
}
