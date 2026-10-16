namespace Nuplane.Reconciliation;

/// <summary>Declares that dry-run planning never opens or inspects a resolved package install path.</summary>
/// <remarks>Implementations may use package and change metadata only.</remarks>
public interface IPackagePathIndependentDryRunPlanner : IDryRunPlanner
{
}
