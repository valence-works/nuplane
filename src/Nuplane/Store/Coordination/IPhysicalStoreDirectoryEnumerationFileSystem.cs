using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Enumerates bounded native child names relative to an already-held directory.</summary>
/// <remarks>
/// Names are observations only. Callers must retain their coordinated owner, open entries no-follow,
/// and revalidate identities and native spelling before using record contents. Enumeration neither
/// establishes an atomic namespace snapshot nor grants authority to delete or follow any entry.
/// </remarks>
internal interface IPhysicalStoreDirectoryEnumerationFileSystem
{
    /// <summary>Copies every direct child basename without following or filtering the child entries.</summary>
    /// <param name="parent">The held directory whose native identity and lookup profile remain stable.</param>
    /// <param name="maximumEntries">A positive upper bound; exceeding it refuses instead of returning a partial list.</param>
    /// <returns>An ordinally sorted detached list of exact names, excluding native dot entries.</returns>
    /// <remarks>
    /// The implementation uses native handle-relative enumeration, retains the parent through the
    /// entire operation, and checks its kind, identity and lookup profile before and after. Each call
    /// uses an independent enumeration cursor. Invalid name encoding/components, duplicate names,
    /// truncated native records and uncertain enumeration outcomes refuse; none means an empty list.
    /// Links and unfamiliar child types remain visible as names for subsequent no-follow inspection.
    /// </remarks>
    IReadOnlyList<string> EnumerateChildNamesNoFollow(
        PhysicalStoreDirectoryHandle parent,
        int maximumEntries);
}
