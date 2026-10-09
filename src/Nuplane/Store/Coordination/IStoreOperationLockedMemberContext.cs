namespace Nuplane.Store.Coordination;

/// <summary>Allows a retained operation owner to expose its exact already-locked registry context internally.</summary>
internal interface IStoreOperationLockedMemberContext
{
    RootMembershipRegistry.LockedMemberLocations LockedMemberLocations { get; }
}
