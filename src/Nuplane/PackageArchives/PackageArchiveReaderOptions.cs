namespace Nuplane.PackageArchives;

/// <summary>Bounds for <see cref="IPackageArchiveReader"/>.</summary>
/// <remarks>Configuration section: <c>Nuplane:PackageArchives</c>.</remarks>
public sealed class PackageArchiveReaderOptions
{
    internal const string SectionName = "PackageArchives";

    /// <summary>Gets or sets the largest archive the reader loads and verifies. Defaults to 256 MiB.</summary>
    public long MaximumArchiveBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>
    /// Gets or sets the byte budget for retaining verified archives. Defaults to 64 MiB.
    /// Zero disables retention of verified archives. Concurrent callers can still share an in-progress load,
    /// and the reader remembers refusals for five seconds. A retry within that window can return
    /// <see cref="PackageArchiveFailureReason.ArchiveMissing"/> after the archive is restored.
    /// </summary>
    public long CacheBytes { get; set; } = 64L * 1024 * 1024;
}
