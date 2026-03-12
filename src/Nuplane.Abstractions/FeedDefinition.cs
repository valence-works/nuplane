namespace Nuplane.Abstractions;

/// <summary>
/// Represents a NuGet feed definition with its connection details and trust level.
/// </summary>
/// <param name="Name">The unique display name of the feed.</param>
/// <param name="ServiceIndex">The NuGet V3 service index URI for this feed.</param>
/// <param name="Credentials">Optional credentials string for authenticated feed access.</param>
public sealed record FeedDefinition(
    string Name,
    Uri ServiceIndex,
    string? Credentials = null);