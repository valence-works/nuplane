namespace Nuplane.Store.Coordination.PhysicalFiles;

/// <summary>Describes the no-follow kind of one filesystem child or held handle.</summary>
internal enum PhysicalStoreEntryKind
{
    /// <summary>A directory entry.</summary>
    Directory,
    /// <summary>A regular file entry.</summary>
    RegularFile,
    /// <summary>A symbolic link entry.</summary>
    SymbolicLink,
    /// <summary>An entry of another or unsupported kind.</summary>
    Other
}
