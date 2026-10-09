using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Metadata;

/// <summary>Reads package metadata under an explicit live root borrow.</summary>
internal interface IScopedPackageMetadataReader : IPackageMetadataReader
{
    NuplanePackageMetadataReadResult Read(string packageId, string version, string installPath,
        PackageStoreOperationBorrow borrow);
}
