namespace Nuplane.Abstractions;

public sealed record FeedDefinition(
    string Name,
    Uri ServiceIndex,
    FeedTrustLevel TrustLevel,
    string? Credentials = null);