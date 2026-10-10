using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Provides bounded streams for package archives and newly created package files.</summary>
/// <remarks>
/// A stream remains bound to the exact held parent and file handles supplied at creation. The provider
/// revalidates the direct child name and native identities around every IO operation and never reopens an
/// absolute path. Read streams are seekable; package-file write streams are exclusive-created and sequential.
/// </remarks>
internal interface IPhysicalStorePackageStreamFileSystem
{
    /// <summary>Opens a bounded, seekable read stream for one verified archive child.</summary>
    /// <param name="parent">The held directory in which the exact archive child was resolved.</param>
    /// <param name="singleName">The exact child component used to open the file.</param>
    /// <param name="file">The provider-owned no-follow file handle.</param>
    /// <param name="expectedParent">The parent identity observed by the caller during resolution.</param>
    /// <param name="expectedFile">The file identity and length observed by the caller during resolution.</param>
    /// <param name="maximumBytes">The explicit maximum archive size.</param>
    /// <returns>A seekable stream that reads from the same native handle and enforces the byte bound.</returns>
    Stream OpenPackageArchiveReadStream(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalStoreFileHandle file,
        PhysicalStoreEntryInfo expectedParent,
        PhysicalStoreEntryInfo expectedFile,
        long maximumBytes);

    /// <summary>Creates a bounded sequential write stream for one exclusively created package-file child.</summary>
    /// <param name="parent">The held directory in which the file was exclusively created.</param>
    /// <param name="singleName">The exact child component used for exclusive creation.</param>
    /// <param name="file">The provider-owned exclusively created file handle.</param>
    /// <param name="expectedParent">The parent identity observed by the caller during creation.</param>
    /// <param name="maximumBytes">The explicit maximum package-file size.</param>
    /// <returns>A sequential stream with one creation-only write claim and a strict byte bound.</returns>
    Stream CreatePackageFileWriteStream(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalStoreFileHandle file,
        PhysicalStoreEntryInfo expectedParent,
        long maximumBytes);
}
