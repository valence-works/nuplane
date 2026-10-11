using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Publishes a prepared directory without replacing any destination entry.</summary>
/// <remarks>
/// The caller owns the held parent, prepares and verifies all directory contents, and closes staged
/// descendants before publication. Initial enrollment requires an explicitly quiescent cutover;
/// competing publishers are resolved by the native atomic no-replace operation. Only an exact
/// canonical source name and verified directory identity may be moved within the same held parent.
/// Observations grant no authority. This operation neither acknowledges enrollment nor removes
/// orphan directories, and supplies no power-loss durability or path-based fallback. Any exception
/// may follow a completed move: preserve evidence and reopen actual entries to resolve the outcome.
/// </remarks>
internal interface IPhysicalStoreDirectoryPublicationFileSystem : IPhysicalStoreDirectoryNameFileSystem
{
    /// <summary>Atomically moves a verified directory to a positively absent same-parent destination.</summary>
    /// <remarks>Never overwrites an existing file, directory, link or alias; returns the reopened exact source identity.</remarks>
    PhysicalStoreEntryInfo PublishDirectoryNoReplaceAt(
        PhysicalStoreDirectoryHandle parent,
        string stagedName,
        PhysicalFileIdentity expectedStagedIdentity,
        string destinationName);
}
