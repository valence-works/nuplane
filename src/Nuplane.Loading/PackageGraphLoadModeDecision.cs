namespace Nuplane.Loading;

internal sealed record PackageGraphLoadModeDecision(
    string GraphKey,
    PackageLoadMode LoadMode,
    IReadOnlyList<PackageLoadModeSelection> Selections,
    IReadOnlyDictionary<string, IReadOnlyList<LoadModeDecisionDiagnostic>> DiagnosticsByPackageKey);
