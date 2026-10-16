namespace Nuplane.Store.Cleanup;

/// <summary>Declares cleanup policy evaluation that never opens or mutates package-store paths.</summary>
public interface IPackagePathIndependentPackageCleanupService : IPackageCleanupService
{
}
