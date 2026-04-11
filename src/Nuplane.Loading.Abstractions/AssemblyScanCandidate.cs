namespace Nuplane.Loading;

/// <summary>
/// Describes one assembly Nuplane recommends for host-owned discovery or scanning.
/// </summary>
/// <param name="AssemblyPath">The full path of the candidate assembly.</param>
/// <param name="AssemblyFileName">The file name of the candidate assembly.</param>
/// <param name="TargetFrameworkMoniker">The selected target framework moniker, when known.</param>
/// <param name="CandidateKind">The candidate role, such as <c>PrimaryLoadAssembly</c>.</param>
/// <param name="SelectionReason">A deterministic reason explaining why the candidate was selected.</param>
internal sealed record AssemblyScanCandidate(
    string AssemblyPath,
    string AssemblyFileName,
    string? TargetFrameworkMoniker,
    string CandidateKind,
    string SelectionReason);

