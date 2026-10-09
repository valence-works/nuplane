namespace Nuplane.Store.Coordination;

/// <summary>Validates an install path against the admitted physical store root.</summary>
internal interface IPackageStoreOperationPathValidator
{
    void ValidateForInstallPath(string installPath);
}
