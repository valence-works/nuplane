using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Publishes or removes single transaction-control files relative to an owned parent.</summary>
/// <remarks>
/// The caller must hold exclusive root/member coordination ownership, close control-file handles
/// that prevent delete sharing, and stage, flush and verify payload/backup evidence before publication.
/// Expected identities are checked around the native transition; they are not an atomic
/// compare-and-swap against non-cooperating filesystem writers. No path fallback, recursive deletion,
/// ledger acknowledgement or power-loss durability is supplied by this contract. Any exception may
/// follow a completed namespace change; the caller must preserve pending evidence and recover by
/// reopening actual content instead of assuming rollback or deleting recovery files.
/// </remarks>
internal interface IPhysicalStorePublicationFileSystem
{
    /// <summary>Atomically moves one verified staged regular file onto a same-parent canonical slot.</summary>
    /// <param name="parent">The provider-owned held parent directory.</param>
    /// <param name="stagedName">The exact canonical basename of the staged single-link file.</param>
    /// <param name="expectedStagedIdentity">The staged file identity verified by the caller.</param>
    /// <param name="destinationName">The exact canonical destination basename, or requested absent basename.</param>
    /// <param name="expectedDestinationIdentity">The expected prior file identity, or null to require atomic no-replace publication.</param>
    /// <returns>The reopened destination metadata, whose identity must match the staged file.</returns>
    PhysicalStoreEntryInfo PublishControlFileAt(
        PhysicalStoreDirectoryHandle parent,
        string stagedName,
        PhysicalFileIdentity expectedStagedIdentity,
        string destinationName,
        PhysicalFileIdentity? expectedDestinationIdentity);

    /// <summary>Removes only one exactly identified, regular, single-link transaction-control entry.</summary>
    /// <param name="parent">The provider-owned held parent directory.</param>
    /// <param name="singleName">The exact canonical basename of the transaction artifact.</param>
    /// <param name="expectedIdentity">The file identity verified by the caller; missing or different entries refuse.</param>
    void RemoveControlFileAt(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedIdentity);
}
