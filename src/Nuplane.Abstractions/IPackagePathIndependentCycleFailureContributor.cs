namespace Nuplane.Abstractions;

/// <summary>Declares that cycle-failure collection reads no package paths or package-store state.</summary>
/// <remarks>This marker allows an enrolled reconciliation to drain diagnostic failures after deferred loading.</remarks>
public interface IPackagePathIndependentCycleFailureContributor : ICycleFailureContributor
{
}
