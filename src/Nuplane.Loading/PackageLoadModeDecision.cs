namespace Nuplane.Loading;

internal sealed record PackageLoadModeDecision(
    PackageLoadModeSelection Selection,
    IReadOnlyList<LoadModeDecisionDiagnostic> Diagnostics);
