using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Describes one requested Windows state-destination directory entry without following a final reparse point.</summary>
internal sealed record WindowsStateDestinationEntryObservation(
    string RequestedName,
    string StoredName,
    string ShortName,
    PhysicalFileIdentity ParentIdentity,
    PhysicalStoreNameSemantics NameSemantics,
    PhysicalStoreEntryInfo Entry);
