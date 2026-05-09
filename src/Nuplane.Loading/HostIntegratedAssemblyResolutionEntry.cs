using System.Reflection;

namespace Nuplane.Loading;

/// <summary>
/// Maps an assembly identity to an active host-integrated package assembly.
/// </summary>
internal sealed record HostIntegratedAssemblyResolutionEntry(
    string AssemblySimpleName,
    string AssemblyFullName,
    Version? Version,
    string AssemblyPath,
    string PackageId,
    string PackageVersion,
    string GraphKey,
    long Generation,
    Assembly Assembly);
