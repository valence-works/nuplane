namespace Nuplane.Abstractions;

/// <summary>
/// Represents a NuGet feed definition with its connection details and trust level.
/// </summary>
/// <param name="Name">The unique display name of the feed.</param>
/// <param name="ServiceIndex">The NuGet V3 service index URI for this feed.</param>
/// <param name="Credentials">
/// Optional secret reference for authenticated feed access, of the form
/// <c>secrets://&lt;provider&gt;/&lt;name&gt;</c>. It names where the secret lives; it is never the secret
/// itself, which is why this record may be logged.
/// </param>
public sealed record FeedDefinition(
    string Name,
    Uri ServiceIndex,
    string? Credentials = null);