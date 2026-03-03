namespace Nuplane.Abstractions;

/// <summary>
/// Represents a package that has been resolved to a concrete version and installed on disk.
/// </summary>
/// <param name="Id">The package identifier.</param>
/// <param name="Version">The resolved concrete version.</param>
/// <param name="FeedName">The name of the feed from which the package was resolved.</param>
/// <param name="InstallPath">The file system path where the package is installed.</param>
/// <param name="InstalledAt">The time at which the package was installed.</param>
/// <param name="SourceName">The name of the desired-state source that requested this package.</param>
public sealed record ResolvedPackage(
    string Id,
    string Version,
    string FeedName,
    string InstallPath,
    DateTimeOffset InstalledAt,
    string SourceName = "");