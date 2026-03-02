namespace Nuplane.Abstractions;

public sealed record PackageLockEntry(
    string Id,
    string Version,
    string Feed,
    string Hash,
    DateTimeOffset Timestamp);