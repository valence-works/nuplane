namespace Nuplane.Abstractions;

/// <summary>
/// The authoritative host-facing description of one currently active reconciled package.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="Version">The active package version.</param>
/// <param name="FeedName">The trusted feed name, when available.</param>
/// <param name="SourceName">The desired-state source name, when available.</param>
/// <param name="InstallPath">The active install path for the package.</param>
/// <param name="ActivatedAtUtc">The UTC timestamp when the package became active.</param>
/// <param name="ActivationCorrelationId">The reconciliation correlation that activated the package.</param>
public sealed record ActivePackageDescriptor(
    string PackageId,
    string Version,
    string? FeedName,
    string? SourceName,
    string InstallPath,
    DateTimeOffset ActivatedAtUtc,
    string ActivationCorrelationId);

