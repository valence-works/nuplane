namespace Nuplane.Abstractions;

public sealed record PackageRequest(
    string Id,
    string VersionRange,
    string? FeedName,
    PackageUpdatePolicy UpdatePolicy,
    string SourceName);