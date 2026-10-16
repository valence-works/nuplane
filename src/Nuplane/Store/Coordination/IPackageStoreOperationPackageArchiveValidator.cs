using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Runs synchronous archive-file work while its exact operation borrow stays validated.</summary>
internal interface IPackageStoreOperationPackageArchiveValidator
{
    TResult WithValidatedPackageArchive<TResult>(
        string installPath,
        Func<IPhysicalStoreFileSystem, PhysicalStoreDirectoryHandle, string, PhysicalStoreFileHandle, TResult> callback);
}
