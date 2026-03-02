namespace Nuplane.Abstractions;

public sealed record PackageLockFile(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<PackageLockEntry> Packages);