namespace Nuplane.Abstractions;

public sealed record ResolvedPackage(
    string Id,
    string Version,
    string FeedName,
    string InstallPath,
    DateTimeOffset InstalledAt,
    string SourceName = "");