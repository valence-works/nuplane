namespace Nuplane.Abstractions;

/// <summary>Declares that a contributor never opens or retains package paths when deriving contributions.</summary>
public interface IPackagePathIndependentDesiredStateContributor : IDesiredStateContributor
{
}
