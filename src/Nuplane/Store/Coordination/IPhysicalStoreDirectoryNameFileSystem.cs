using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Observes native canonical directory-entry names without following links.</summary>
internal interface IPhysicalStoreDirectoryNameFileSystem
{
    /// <summary>Observes a non-link directory's exact stored basename and native parent name profile.</summary>
    /// <remarks>Revalidates the held parent and expected directory identity without following the final entry.</remarks>
    PhysicalStoreCanonicalName ObserveCanonicalDirectoryNameNoFollow(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedDirectoryIdentity);
}
