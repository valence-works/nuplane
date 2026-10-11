namespace Nuplane.Abstractions;

/// <summary>Declares that an observer never opens or retains package paths in any lifecycle callback.</summary>
/// <remarks>This is an explicit participation promise; it does not intercept arbitrary filesystem calls.</remarks>
public interface IPackagePathIndependentNuplaneObserver : INuplaneObserver
{
}
