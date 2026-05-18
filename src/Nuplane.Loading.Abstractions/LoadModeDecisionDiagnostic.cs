namespace Nuplane.Loading;

/// <summary>
/// Explains why Nuplane selected an effective package or graph load mode.
/// </summary>
/// <param name="GraphKey">The deterministic loading graph key.</param>
/// <param name="EffectiveGraphLoadMode">The final graph load mode.</param>
/// <param name="EffectivePackageLoadMode">The final package load mode.</param>
/// <param name="ReasonCode">The stable machine-readable reason code.</param>
/// <param name="DeclaringPackageId">The package that declared the requirement, when available.</param>
/// <param name="DeclaringPackageVersion">The declaring package version, when available.</param>
/// <param name="RequestedScope">The requested metadata/advisor scope, when available.</param>
/// <param name="AdvisorName">The advisor that produced the decision input, when available.</param>
/// <param name="Message">A bounded, secret-safe message.</param>
public sealed record LoadModeDecisionDiagnostic(
    string GraphKey,
    PackageLoadMode EffectiveGraphLoadMode,
    PackageLoadMode EffectivePackageLoadMode,
    string ReasonCode,
    string? DeclaringPackageId = null,
    string? DeclaringPackageVersion = null,
    string? RequestedScope = null,
    string? AdvisorName = null,
    string? Message = null);
