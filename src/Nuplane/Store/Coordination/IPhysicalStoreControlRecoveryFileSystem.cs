using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Performs bounded native recovery operations for single control files.</summary>
/// <remarks>
/// Removal capabilities own one no-follow file handle and its non-waiting exclusive lock. They are
/// provider-bound, one-shot, and never authorize package-content or recursive deletion.
/// </remarks>
internal interface IPhysicalStoreControlRecoveryFileSystem
{
    /// <summary>Opens an exactly identified control file and makes one non-waiting exclusive-lock attempt.</summary>
    /// <returns>A provider-owned locked token, or null only when the native lock was positively busy.</returns>
    ValueTask<PhysicalStoreLockedControlFile?> TryOpenAndLockControlFileForRemovalAt(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedIdentity);

    /// <summary>Removes the exact file held by the supplied lock capability and consumes the token.</summary>
    /// <remarks>An error can occur after a native namespace mutation; reopen current evidence before retrying.</remarks>
    ValueTask RemoveLockedControlFileAsync(PhysicalStoreLockedControlFile file);

    /// <summary>Moves one verified regular single-link control file to an absent same-parent name without replacement.</summary>
    PhysicalStoreEntryInfo MoveControlFileNoReplaceAt(
        PhysicalStoreDirectoryHandle parent,
        string sourceName,
        PhysicalFileIdentity expectedSourceIdentity,
        string destinationName);
}
