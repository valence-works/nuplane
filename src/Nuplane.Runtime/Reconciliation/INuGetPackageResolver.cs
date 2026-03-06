using Nuplane.Abstractions;

namespace Nuplane.Runtime.Reconciliation;

/// <summary>
/// Specialized package resolver contract for NuGet-based resolution.
/// </summary>
public interface INuGetPackageResolver : IPackageResolver
{
}