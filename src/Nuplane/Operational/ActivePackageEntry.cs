namespace Nuplane.Operational;

/// <summary>
/// Represents an active package entry in the operational snapshot.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="Version">The active version.</param>
public sealed record ActivePackageEntry(string PackageId, string Version);