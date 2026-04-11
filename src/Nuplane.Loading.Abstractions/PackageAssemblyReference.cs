namespace Nuplane.Loading;

/// <summary>
/// Durable description of an assembly associated with an active package.
/// </summary>
/// <param name="AssemblyPath">The full path of the assembly.</param>
/// <param name="AssemblyFileName">The file name of the assembly.</param>
/// <param name="TargetFrameworkMoniker">The selected target framework moniker, when known.</param>
/// <param name="Kind">The assembly role, such as <c>PrimaryLoadAssembly</c>.</param>
/// <param name="SelectionReason">The deterministic reason the assembly was selected.</param>
public sealed record PackageAssemblyReference(
    string AssemblyPath,
    string AssemblyFileName,
    string? TargetFrameworkMoniker,
    string Kind,
    string SelectionReason)
{
    /// <summary>
    /// Creates a canonical assembly reference from the legacy scan-candidate model.
    /// </summary>
    public static PackageAssemblyReference FromCandidate(AssemblyScanCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return new PackageAssemblyReference(
            candidate.AssemblyPath,
            candidate.AssemblyFileName,
            candidate.TargetFrameworkMoniker,
            candidate.CandidateKind,
            candidate.SelectionReason);
    }

    /// <summary>
    /// Converts this canonical assembly reference back to the legacy scan-candidate model.
    /// </summary>
    public AssemblyScanCandidate ToCandidate() =>
        new(
            AssemblyPath,
            AssemblyFileName,
            TargetFrameworkMoniker,
            Kind,
            SelectionReason);
}

