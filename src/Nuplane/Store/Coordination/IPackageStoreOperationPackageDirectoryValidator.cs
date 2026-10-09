using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Runs synchronous package-directory work while its exact operation borrow stays validated.</summary>
internal interface IPackageStoreOperationPackageDirectoryValidator
{
    TResult WithValidatedPackageDirectory<TResult>(
        string installPath,
        Func<IPhysicalStoreFileSystem, PhysicalStoreDirectoryHandle, TResult> callback);
}
