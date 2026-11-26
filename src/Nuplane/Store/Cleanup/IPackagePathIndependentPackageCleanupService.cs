namespace Nuplane.Store.Cleanup;

/// <summary>Declares cleanup policy evaluation that never opens or mutates package-store paths.</summary>
internal interface IPackagePathIndependentPackageCleanupService : IPackageCleanupService
{
}
