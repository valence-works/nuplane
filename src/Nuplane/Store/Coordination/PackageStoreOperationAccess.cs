using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Coordination;

/// <summary>Validates owner/borrow identity before exposing existing root/member locks to coordinated writers.</summary>
internal static class PackageStoreOperationAccess
{
    internal static PackageStoreOperationOwner GetOwner(PackageStoreOperationBorrow borrow)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        var owner = borrow.Owner;
        _ = GetLockedMemberLocations(owner, borrow);
        return owner;
    }

    internal static RootMembershipRegistry.LockedMemberLocations GetLockedMemberLocations(
        PackageStoreOperationBorrow borrow)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        return GetLockedMemberLocations(borrow.Owner, borrow);
    }

    internal static RootMembershipRegistry.LockedMemberLocations GetLockedMemberLocations(
        PackageStoreOperationOwner owner,
        PackageStoreOperationBorrow borrow)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(borrow);
        if (!ReferenceEquals(borrow.Owner, owner) || !ReferenceEquals(borrow.Control, owner.Control) || borrow.IsDisposed)
        {
            throw new PackageStoreAdmissionException(
                PackageStoreAdmissionReason.ExpiredScope,
                "The borrow does not identify the exact live owner for registry publication.",
                borrow.Root);
        }

        if (owner.Control is not PackageStoreOperationState state)
        {
            throw new PackageStoreAdmissionException(
                PackageStoreAdmissionReason.UnsupportedParticipant,
                "The owner does not expose a coordinated registry publication context.",
                owner.Root);
        }

        return state.GetLockedMemberLocations(owner, borrow);
    }
}
