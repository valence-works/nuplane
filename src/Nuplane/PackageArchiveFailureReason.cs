namespace Nuplane;

/// <summary>Why <see cref="IPackageArchiveReader"/> refused a package archive.</summary>
public enum PackageArchiveFailureReason
{
    /// <summary>The active package has no recorded archive hash, for example because an earlier Nuplane version activated it.</summary>
    HashNotRecorded,

    /// <summary>The acquired archive is not where Nuplane keeps it, for example because an earlier Nuplane version installed the package or the file was removed.</summary>
    ArchiveMissing,

    /// <summary>The archive is larger than <see cref="PackageArchiveReaderOptions.MaximumArchiveBytes"/>.</summary>
    ArchiveTooLarge,

    /// <summary>The archive exists but could not be read, for example because access was denied or it is not a file.</summary>
    ArchiveUnreadable,

    /// <summary>The archive bytes do not match the recorded sha512: it was modified or replaced.</summary>
    HashMismatch,

    /// <summary>The archive matches its recorded hash but is not a readable zip archive.</summary>
    InvalidArchive
}
