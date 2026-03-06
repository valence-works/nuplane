namespace Nuplane.Store.State;

/// <summary>
/// Represents a package version entry used as input for cleanup evaluation.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="Version">The package version.</param>
/// <param name="CapturedAt">The time the version was first captured.</param>
/// <param name="IsLastKnownGood">Whether this version is the last-known-good version.</param>
public sealed record PackageVersionEntry(
    string PackageId,
    string Version,
    DateTimeOffset CapturedAt,
    bool IsLastKnownGood);