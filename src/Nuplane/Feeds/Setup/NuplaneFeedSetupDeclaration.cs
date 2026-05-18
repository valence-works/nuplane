namespace Nuplane.Feeds.Setup;

/// <summary>
/// Represents one effective setup feed declaration after configuration shape classification.
/// </summary>
/// <param name="Name">The canonical feed name used for registration.</param>
/// <param name="SourceShape">The configuration shape used to declare the feed.</param>
/// <param name="ConfigurationPath">The configuration path for diagnostics.</param>
/// <param name="ArrayIndex">The numeric array index when the declaration came from an array entry.</param>
/// <param name="Key">The keyed feed name when the declaration came from a keyed entry.</param>
/// <param name="Options">The feed setup options bound from the declaration.</param>
/// <param name="IgnoredArrayDeclarations">Array declarations ignored because a keyed declaration with the same name wins.</param>
public sealed record NuplaneFeedSetupDeclaration(
    string Name,
    NuplaneFeedSetupSourceShape SourceShape,
    string ConfigurationPath,
    int? ArrayIndex,
    string? Key,
    NuplaneFeedSetupOptions Options,
    IReadOnlyList<NuplaneFeedSetupDeclaration> IgnoredArrayDeclarations);
