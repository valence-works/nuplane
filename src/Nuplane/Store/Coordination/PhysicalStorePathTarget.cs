namespace Nuplane.Store.Coordination;

/// <summary>Identifies the expected kind and final-link policy for one configured store locator.</summary>
internal enum PhysicalStorePathTarget
{
    /// <summary>A configured root directory, whose final alias may be expanded and physically verified.</summary>
    ConfiguredRootDirectory,

    /// <summary>A configured root directory that may have a verified missing suffix while unenrolled.</summary>
    ConfiguredRootDirectoryAllowMissingSuffix,

    /// <summary>A package or member directory, whose final alias is refused.</summary>
    PackageDirectory,

    /// <summary>A regular single-link archive file, whose final alias is refused.</summary>
    ArchiveFile
}
