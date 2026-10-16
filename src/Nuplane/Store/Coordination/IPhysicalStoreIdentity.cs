using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Builds descriptive stable state-slot identities from native observations.</summary>
internal interface IPhysicalStoreIdentity
{
    /// <summary>Identifies an existing regular, single-link state file in a held directory.</summary>
    /// <remarks>The caller coordinates mutations; this does not create, enroll or authorize a state.</remarks>
    PhysicalStoreStateSlotObservation ObserveStateSlot(PhysicalStoreDirectoryHandle parent, string singleName);
}
