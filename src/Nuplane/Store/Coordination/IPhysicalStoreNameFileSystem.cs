using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Observes native directory lookup semantics and exact single-link file names.</summary>
/// <remarks>Observations grant no authority; callers coordinate mutations and revalidate before publication.</remarks>
internal interface IPhysicalStoreNameFileSystem
{
    /// <summary>Observes the native lookup profile of an existing held directory.</summary>
    /// <remarks>
    /// The provider verifies the held directory kind and identity, then confirms that the native profile remains
    /// stable through the observation. The returned profile is metadata only: it grants no admission, authority,
    /// or permission to create, probe, or reopen entries by path. Unsupported or ambiguous profile evidence is a
    /// typed refusal, never a guessed comparer.
    /// </remarks>
    PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent);

    /// <summary>Resolves a supplied component to its actual stored basename without following the final entry.</summary>
    /// <remarks>
    /// The provider verifies the expected file and held parent identities, single-link regular-file type,
    /// canonical entry spelling and name profile across the observation. A returned full native path
    /// may supply a leaf candidate only; it must never supply authority or be reopened as a path.
    /// Unsupported or ambiguous spelling/profile evidence is a typed refusal, never a guessed comparer.
    /// </remarks>
    PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedFileIdentity);
}
