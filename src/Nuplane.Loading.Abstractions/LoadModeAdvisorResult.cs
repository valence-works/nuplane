namespace Nuplane.Loading;

/// <summary>
/// Represents one package load-mode advisor result.
/// </summary>
/// <param name="AdvisorName">The stable advisor name.</param>
/// <param name="PackageId">The package that declared or produced the result.</param>
/// <param name="Version">The package version.</param>
/// <param name="RequestedLoadMode">The requested or preferred load mode.</param>
/// <param name="Scope">The requested scope, such as <c>DependencyClosure</c> or <c>PackageOnly</c>.</param>
/// <param name="ReasonCode">The stable machine-readable reason code.</param>
/// <param name="Reason">The optional human-readable reason.</param>
/// <param name="IsValid">Whether this result can influence effective mode selection.</param>
/// <param name="Diagnostic">The optional diagnostic for invalid or degraded advisor results.</param>
public sealed record LoadModeAdvisorResult(
    string AdvisorName,
    string PackageId,
    string Version,
    PackageLoadMode RequestedLoadMode,
    string Scope,
    string ReasonCode,
    string? Reason,
    bool IsValid = true,
    string? Diagnostic = null);
