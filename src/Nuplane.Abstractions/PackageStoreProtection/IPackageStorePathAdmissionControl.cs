namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>Internal core implementation boundary for path-admission handles.</summary>
internal interface IPackageStorePathAdmissionControl
{
    PackageStoreOperationBorrow BorrowFor(PackageStorePathAdmission admission, string installPath);
    ValueTask DisposeAsync(PackageStorePathAdmission admission);
}
