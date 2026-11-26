namespace Nuplane.PackageArchives;

/// <summary>One file entry of a <see cref="PackageArchive"/>.</summary>
public sealed class PackageArchiveEntry
{
    private readonly PackageArchive _archive;

    internal PackageArchiveEntry(
        PackageArchive archive,
        int index,
        string path,
        long length,
        DateTimeOffset lastWriteTime)
    {
        _archive = archive;
        Index = index;
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

    /// <summary>Decompresses the entry.</summary>
    /// <returns>Exactly <see cref="Length"/> bytes.</returns>
    /// <exception cref="InvalidDataException">
    /// The entry is corrupt, uses unsupported compression, exceeds the maximum byte array length, or its
    /// data does not match its declared size.
    /// </exception>
    public byte[] ReadAllBytes() => _archive.ReadEntry(this);
}
