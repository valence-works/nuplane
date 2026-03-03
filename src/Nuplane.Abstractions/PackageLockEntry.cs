namespace Nuplane.Abstractions;

/// <summary>
/// Represents a single entry in a package lock file, pinning a package to a specific version and hash.
/// </summary>
/// <param name="Id">The package identifier.</param>
/// <param name="Version">The locked package version.</param>
/// <param name="Feed">The feed from which the package was resolved.</param>
/// <param name="Hash">The integrity hash of the package.</param>
/// <param name="Timestamp">The time at which this lock entry was created.</param>
public sealed record PackageLockEntry(
    string Id,
    string Version,
    string Feed,
    string Hash,
    DateTimeOffset Timestamp);