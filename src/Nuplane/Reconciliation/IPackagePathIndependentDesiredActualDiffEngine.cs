namespace Nuplane.Reconciliation;

/// <summary>Declares that diff computation never opens or inspects a resolved package install path.</summary>
/// <remarks>
/// Implementations may compare package identifiers, versions, and other in-memory metadata only.
/// Both diff computation and next-version projection must honor this promise.
/// </remarks>
public interface IPackagePathIndependentDesiredActualDiffEngine : IDesiredActualDiffEngine
{
}
