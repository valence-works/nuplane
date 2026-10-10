using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Validates owner/borrow identity before exposing existing root/member locks to coordinated writers.</summary>
internal static class PackageStoreOperationAccess
{
    /// <summary>Runs core work through the existing root/member owner, retaining it across awaits.</summary>
    internal static Task<TResult> WithValidatedRootAsync<TResult>(
        PackageStoreOperationBorrow borrow,
        Func<IPhysicalStoreFileSystem, PhysicalStoreDirectoryHandle, CancellationToken, Task<TResult>> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        ArgumentNullException.ThrowIfNull(callback);
        if (borrow.Control is not PackageStoreOperationState state)
        {
            throw new PackageStoreAdmissionException(
                PackageStoreAdmissionReason.UnsupportedParticipant,
                "The operation owner does not provide retained native root access.",
                borrow.Root);
        }

        return state.WithValidatedRootAsync(borrow, callback, cancellationToken);
    }

    internal static TResult WithValidatedPackageDirectory<TResult>(
        PackageStoreOperationBorrow borrow,
        string installPath,
        Func<IPhysicalStoreFileSystem, PhysicalStoreDirectoryHandle, TResult> callback)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        ArgumentNullException.ThrowIfNull(callback);
        _ = GetOwner(borrow);
        if (borrow.Control is not PackageStoreOperationState state)
        {
            throw new PackageStoreAdmissionException(
                PackageStoreAdmissionReason.UnsupportedParticipant,
                "The operation owner does not provide native package-directory access.",
                borrow.Root);
        }

        return state.WithValidatedPackageDirectory(borrow, installPath, callback);
    }

    internal static TResult WithValidatedPackageDirectoryOrMissing<TResult>(
        PackageStoreOperationBorrow borrow,
        string installPath,
        Func<IPhysicalStoreFileSystem, PhysicalStoreDirectoryHandle?, TResult> callback)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        ArgumentNullException.ThrowIfNull(callback);
        _ = GetOwner(borrow);
        if (borrow.Control is not PackageStoreOperationState state)
        {
            throw new PackageStoreAdmissionException(
                PackageStoreAdmissionReason.UnsupportedParticipant,
                "The admitted operation cannot expose native package-directory or positive-absence evidence.",
                borrow.Root);
        }

        return state.WithValidatedPackageDirectoryOrMissing(borrow, installPath, callback);
    }

    internal static TResult WithValidatedPackageArchive<TResult>(
        PackageStoreOperationBorrow borrow,
        string installPath,
        Func<IPhysicalStoreFileSystem, PhysicalStoreDirectoryHandle, string, PhysicalStoreFileHandle, TResult> callback)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        ArgumentNullException.ThrowIfNull(callback);
        _ = GetOwner(borrow);
        if (borrow.Control is not PackageStoreOperationState state)
        {
            throw new PackageStoreAdmissionException(
                PackageStoreAdmissionReason.UnsupportedParticipant,
                "The admitted operation cannot expose a held package archive for scoped reads.",
                borrow.Root);
        }

        return state.WithValidatedPackageArchive(borrow, installPath, callback);
    }

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
