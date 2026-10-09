using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Performs no-follow filesystem operations relative to validated held directory handles.</summary>
/// <remarks>
/// This is an internal core contract. It deliberately contains no rename or recursive-deletion operation;
/// atomic state replacement and physical deletion require separately reviewed contracts.
/// Every child-name parameter is one exact component and must pass
/// <see cref="PhysicalStoreNames.ValidateSingleComponent"/> before reaching a native adapter.
/// </remarks>
internal interface IPhysicalStoreFileSystem
{
    /// <summary>Opens an operating-system namespace root.</summary>
    /// <param name="anchor">A strict namespace-root anchor, such as Unix <c>/</c> or a Windows drive or qualified volume root.</param>
    /// <returns>An owned handle to the namespace root identified by the exact anchor.</returns>
    PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor);

    /// <summary>Inspects one direct child without following its final link.</summary>
    /// <param name="parent">The held parent directory.</param>
    /// <param name="singleName">One validated child name.</param>
    /// <returns>Metadata, or null only when absence was positively established.</returns>
    PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName);

    /// <summary>Opens one direct child directory without following its final link.</summary>
    PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName);

    /// <summary>Opens the held directory's parent directory without resolving through a child link.</summary>
    PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory);

    /// <summary>Opens one direct child file without following its final link.</summary>
    PhysicalStoreFileHandle OpenFileChildNoFollow(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        FileAccess access);

    /// <summary>Reads a symbolic-link target without following the link.</summary>
    /// <param name="parent">The held parent directory.</param>
    /// <param name="singleName">The validated symbolic-link name.</param>
    /// <param name="expectedLinkIdentity">The expected no-follow identity used to detect replacement.</param>
    /// <returns>The link target as reported by the provider.</returns>
    /// <remarks>The adapter verifies the link identity before and after reading the target and refuses a change.</remarks>
    string ReadLinkTargetNoFollow(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedLinkIdentity);

    /// <summary>Inspects the object represented by a held handle.</summary>
    PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle);

    /// <summary>Creates a direct child directory only if absent, then verifies its observed identity across reopen.</summary>
    /// <returns>The observed directory opened and verified relative to the held parent.</returns>
    /// <remarks>
    /// The caller must coordinate cooperating mutations. The adapter compares no-follow observations before
    /// and after reopen; this is not an atomic create-and-open guarantee against arbitrary filesystem writers.
    /// </remarks>
    PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName);

    /// <summary>Creates and returns a new direct child file, refusing to replace any existing object.</summary>
    /// <remarks>The returned file handle is marked as exclusively created and may claim one initial write.</remarks>
    PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName);

    /// <summary>Reads a bounded copy of a control file's bytes.</summary>
    /// <param name="file">The held control file.</param>
    /// <param name="maximumBytes">The maximum permitted byte count.</param>
    /// <returns>A copied byte array no larger than the requested limit.</returns>
    byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes);

    /// <summary>Writes new control-file contents and flushes them through the held handle.</summary>
    /// <remarks>The adapter claims the handle's one-time creation-only write; this operation never replaces existing content.</remarks>
    void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents);

    /// <summary>Makes one non-waiting attempt to acquire an exclusive lock on the held file.</summary>
    /// <param name="file">The held lock file.</param>
    /// <returns>A lock owner, or null only when the adapter positively established that the lock is busy.</returns>
    /// <remarks>Waiting, retries and polling policy belong to the caller, not the filesystem adapter.</remarks>
    ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file);
}
