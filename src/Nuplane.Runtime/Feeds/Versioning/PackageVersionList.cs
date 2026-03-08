namespace Nuplane.Runtime.Feeds.Versioning;

/// <summary>
/// An ordered list of available versions for a given package from a specific feed.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="FeedName">The feed that produced this list.</param>
/// <param name="Versions">The available version strings, sorted ascending by SemVer.</param>
/// <param name="EnumeratedAt">The timestamp when the versions were enumerated.</param>
internal sealed record PackageVersionList(
    string PackageId,
    string FeedName,
    IReadOnlyList<string> Versions,
    DateTimeOffset EnumeratedAt);
