namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>Internal core implementation boundary for opaque operation owners and borrows.</summary>
internal interface IPackageStoreOperationOwnerControl
{
    PackageStoreOperationBorrow Borrow(PackageStoreOperationOwner owner);
    ValueTask DisposeOwnerAsync(PackageStoreOperationOwner owner);
    void ReleaseBorrow(PackageStoreOperationBorrow borrow);
    void ValidateForInstallPath(PackageStoreOperationBorrow borrow, string installPath);
}
