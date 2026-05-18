namespace Nuplane.Feeds.Setup;

/// <summary>
/// Contains effective setup feed declarations and diagnostics from configuration reading.
/// </summary>
/// <param name="Declarations">The effective setup feed declarations.</param>
/// <param name="Diagnostics">Warnings and errors discovered while reading setup feed configuration.</param>
public sealed record NuplaneFeedSetupReadResult(
    IReadOnlyList<NuplaneFeedSetupDeclaration> Declarations,
    IReadOnlyList<NuplaneFeedSetupDiagnostic> Diagnostics)
{
    /// <summary>
    /// Gets an empty read result.
    /// </summary>
    public static NuplaneFeedSetupReadResult Empty { get; } = new([], []);
}
