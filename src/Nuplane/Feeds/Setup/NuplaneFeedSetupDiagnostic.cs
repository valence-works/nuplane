namespace Nuplane.Feeds.Setup;

/// <summary>
/// Describes a setup feed configuration warning or error.
/// </summary>
/// <param name="Severity">The diagnostic severity.</param>
/// <param name="Code">The stable diagnostic code.</param>
/// <param name="Message">The human-readable diagnostic message.</param>
/// <param name="ConfigurationPath">The configuration path associated with the diagnostic.</param>
/// <param name="FeedName">The feed name associated with the diagnostic, when available.</param>
public sealed record NuplaneFeedSetupDiagnostic(
    NuplaneFeedSetupDiagnosticSeverity Severity,
    string Code,
    string Message,
    string ConfigurationPath,
    string? FeedName);
