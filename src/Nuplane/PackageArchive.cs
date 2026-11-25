using System.Collections.ObjectModel;
using System.IO.Compression;

namespace Nuplane;

/// <summary>
/// The verified <c>.nupkg</c> of one active package, held in memory. Its bytes matched the package's recorded
/// sha512 when it was opened, so every entry it returns is the admitted package's content.
/// </summary>
/// <remarks>
/// Entry paths are package-relative and use <c>/</c>; names are used as the archive stores them, as Nuplane
/// extracts them. Lookup is case-insensitive, like NuGet package paths. Directory entries, entries whose names would escape the
/// package (rooted, <c>..</c>, <c>.</c>, empty segments, <c>:</c> or NUL), and names that two entries share
/// after normalization are not exposed. Instances are immutable and safe to use from several threads.
/// </remarks>
public sealed class PackageArchive
{
    private readonly byte[] _bytes;
    private readonly Dictionary<string, PackageArchiveEntry> _entriesByPath;

    private PackageArchive(string packageId, string version, string archiveHash, byte[] bytes)
    {
        PackageId = packageId;
        Version = version;
        ArchiveHash = archiveHash;
        _bytes = bytes;

        using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        var entries = new Dictionary<string, PackageArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < archive.Entries.Count; index++)
        {
            var entry = archive.Entries[index];
            if (!TryNormalizeEntryName(entry.FullName, out var path))
                continue;
            if (ambiguous.Contains(path))
                continue;
            if (!entries.TryAdd(path, new PackageArchiveEntry(this, index, entry.FullName, path, entry.Length, entry.LastWriteTime)))
            {
                entries.Remove(path);
                ambiguous.Add(path);
            }
        }

        _entriesByPath = entries;
        Entries = new ReadOnlyCollection<PackageArchiveEntry>(
            entries.Values.OrderBy(static entry => entry.Index).ToArray());
    }

    /// <summary>Gets the package identifier.</summary>
    public string PackageId { get; }

    /// <summary>Gets the package version.</summary>
    public string Version { get; }

    /// <summary>Gets the verified archive hash, <c>sha512:&lt;base64&gt;</c>.</summary>
    public string ArchiveHash { get; }

    /// <summary>Gets the size of the verified archive in bytes.</summary>
    public long Length => _bytes.LongLength;

    /// <summary>Gets the exposed file entries in archive order.</summary>
    public IReadOnlyList<PackageArchiveEntry> Entries { get; }

    /// <summary>Finds an exposed entry by its package-relative path.</summary>
    /// <param name="relativePath">A path using <c>/</c> or <c>\</c>; it must stay inside the package.</param>
    /// <returns>The entry, or <see langword="null"/> when the path is invalid, absent or ambiguous.</returns>
    public PackageArchiveEntry? GetEntry(string relativePath)
    {
        if (!TryNormalizeRequestPath(relativePath, out var path))
            return null;
        return _entriesByPath.GetValueOrDefault(path);
    }

    internal static PackageArchive Load(string packageId, string version, string archiveHash, byte[] bytes) =>
        new(packageId, version, archiveHash, bytes);

    internal byte[] ReadEntry(PackageArchiveEntry expected)
    {
        if (expected.Length > Array.MaxLength)
            throw new InvalidDataException("The package archive entry is larger than a managed byte array can represent.");

        using var archive = new ZipArchive(new MemoryStream(_bytes, writable: false), ZipArchiveMode.Read);
        var entry = archive.Entries[expected.Index];
        if (!string.Equals(entry.FullName, expected.EntryName, StringComparison.Ordinal))
            throw new InvalidDataException("The package archive entry table changed.");

        var bytes = new byte[(int)expected.Length];
        using var stream = entry.Open();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
                throw new InvalidDataException("The package archive entry ended before its declared length.");
            offset += read;
        }

        if (stream.ReadByte() != -1)
            throw new InvalidDataException("The package archive entry exceeded its declared length.");
        return bytes;
    }

    internal static bool TryNormalizeRequestPath(string? relativePath, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(relativePath))
            return false;
        return TryNormalizeSegments(relativePath.Replace('\\', '/'), out normalized);
    }

    internal static bool TryNormalizeEntryName(string entryName, out string normalized) =>
        TryNormalizeRequestPath(entryName, out normalized);

    private static bool TryNormalizeSegments(string path, out string normalized)
    {
        normalized = string.Empty;
        if (path.Contains(':') || path.Contains('\0'))
            return false;

        var segments = path.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
                return false;
        }

        normalized = path;
        return true;
    }
}

/// <summary>One file entry of a <see cref="PackageArchive"/>.</summary>
public sealed class PackageArchiveEntry
{
    private readonly PackageArchive _archive;

    internal PackageArchiveEntry(
        PackageArchive archive,
        int index,
        string entryName,
        string path,
        long length,
        DateTimeOffset lastWriteTime)
    {
        _archive = archive;
        Index = index;
        EntryName = entryName;
        Path = path;
        Length = length;
        LastWriteTime = lastWriteTime;
    }

    /// <summary>Gets the normalized package-relative path, using <c>/</c>.</summary>
    public string Path { get; }

    /// <summary>Gets the uncompressed size the archive declares for this entry.</summary>
    public long Length { get; }

    /// <summary>Gets the modification time the archive records for this entry.</summary>
    public DateTimeOffset LastWriteTime { get; }

    internal int Index { get; }

    internal string EntryName { get; }

    /// <summary>Decompresses the entry.</summary>
    /// <returns>Exactly <see cref="Length"/> bytes.</returns>
    /// <exception cref="InvalidDataException">The entry's data does not match its declared size.</exception>
    public byte[] ReadAllBytes() => _archive.ReadEntry(this);
}
