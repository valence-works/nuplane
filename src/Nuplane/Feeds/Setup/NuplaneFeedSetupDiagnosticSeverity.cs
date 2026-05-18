namespace Nuplane.Feeds.Setup;

/// <summary>
/// Identifies the severity of a setup feed diagnostic.
/// </summary>
public enum NuplaneFeedSetupDiagnosticSeverity
{
    /// <summary>
    /// The diagnostic describes a non-fatal configuration condition.
    /// </summary>
    Warning,

    /// <summary>
    /// The diagnostic describes a configuration error that should fail validation.
    /// </summary>
    Error
}
