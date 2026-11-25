namespace Nuplane;

/// <summary>Bounds for <see cref="IPackageArchiveReader"/>.</summary>
/// <remarks>Configuration section: <c>Nuplane:PackageArchives</c>.</remarks>
public sealed class PackageArchiveReaderOptions
{
    internal const string SectionName = "PackageArchives";

    /// <summary>Gets or sets the largest archive the reader loads and verifies. Defaults to 256 MiB.</summary>
    public long MaximumArchiveBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>
    /// Gets or sets how many bytes of verified archives the reader keeps in memory. Defaults to 64 MiB.
    /// Zero disables caching, so every open reads and verifies the archive again.
    /// </summary>
    public long CacheBytes { get; set; } = 64L * 1024 * 1024;
}
