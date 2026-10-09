using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Coordination;

/// <summary>Pairs a stable slot with the current file identity observed at that slot.</summary>
internal sealed record PhysicalStoreStateSlotObservation(StateSlotIdentity Slot, PhysicalFileIdentity FileIdentity);
