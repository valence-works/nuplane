namespace Nuplane.Loading;

/// <summary>
/// Describes the outcome of a host-integrated assembly resolution decision.
/// </summary>
internal sealed record HostIntegratedAssemblyResolutionDiagnostic(
    string RequestedAssemblyName,
    string Outcome,
    string? SelectedAssemblyPath,
    IReadOnlyList<string> CandidateAssemblies,
    string Message);
