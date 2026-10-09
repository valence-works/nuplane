namespace Nuplane.Reconciliation;

/// <summary>Declares that lock-file policy never opens or inspects a resolved package install path.</summary>
/// <remarks>Policy may consume resolved package metadata and the lock-file store.</remarks>
public interface IPackagePathIndependentLockFileCoordinator : ILockFileCoordinator
{
}
