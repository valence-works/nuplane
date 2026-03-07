namespace Nuplane.Builder;

/// <summary>
/// Internal registration-time snapshot of a feed's source-trust-relevant shape.
/// Used to compose runtime options without keeping mutable builder state alive after registration.
/// </summary>
internal sealed record NuplaneFeedRegistration(
    string Name,
    IReadOnlyList<string> IncludePatterns,
    bool HasExplicitUnrestrictedPackageSelection);

