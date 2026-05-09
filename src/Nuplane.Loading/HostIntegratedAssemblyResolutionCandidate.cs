namespace Nuplane.Loading;

/// <summary>
/// Describes a host-integrated assembly identity before its assembly is loaded.
/// </summary>
internal sealed record HostIntegratedAssemblyResolutionCandidate(
    string AssemblySimpleName,
    Version? Version,
    string PackageId,
    string PackageVersion,
    string GraphKey);
