using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Removes one exact no-follow entry relative to a provider-owned held parent.</summary>
/// <remarks>
/// This internal primitive grants no maintenance authority. The executor must retain validated
/// root/all-state ownership and verified eligible-install or quarantine containment throughout IO.
/// Each basename must be the exact native canonical spelling of the supplied identity. Linked,
/// reparse, foreign-volume, missing or changed entries refuse. No path fallback or recursive walk
/// is provided. An empty-directory removal must atomically refuse a nonempty directory.
/// Success requires positive entry absence after native removal and handle close. An exception may
/// follow a completed mutation; callers must inspect actual evidence and preserve partial facts.
/// Checks serialize cooperating owners and do not claim atomic identity compare-and-swap against
/// arbitrary filesystem writers outside the coordinated-store contract.
/// </remarks>
internal interface IPhysicalStoreDeletionFileSystem
{
    /// <summary>Removes only the exact regular single-link file at the canonical basename.</summary>
    void RemoveFileAt(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedIdentity);

    /// <summary>Removes only the exact empty directory at the canonical basename.</summary>
    void RemoveEmptyDirectoryAt(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedIdentity);
}
