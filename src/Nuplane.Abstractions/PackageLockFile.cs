namespace Nuplane.Abstractions;

/// <summary>
/// Represents a package lock file containing pinned package versions and their integrity hashes.
/// </summary>
/// <param name="SchemaVersion">The schema version of the lock file format.</param>
/// <param name="GeneratedAt">The time at which the lock file was generated.</param>
/// <param name="Packages">The list of locked package entries.</param>
public sealed record PackageLockFile(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<PackageLockEntry> Packages);