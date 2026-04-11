namespace Nuplane.Abstractions;

/// <summary>
/// The canonical host-facing representation of one currently active reconciled package.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="Version">The active package version.</param>
/// <param name="FeedName">The trusted feed name, when available.</param>
/// <param name="SourceName">The desired-state source name, when available.</param>
/// <param name="InstallPath">The active install path for the package.</param>
/// <param name="ActivatedAtUtc">The UTC timestamp when the package became active.</param>
/// <param name="ActivationCorrelationId">The reconciliation correlation that activated the package.</param>
public sealed record ActivePackage(
    string PackageId,
    string Version,
    string? FeedName,
    string? SourceName,
    string InstallPath,
    DateTimeOffset ActivatedAtUtc,
    string ActivationCorrelationId)
{
    /// <summary>
    /// Creates the canonical active-package model from the legacy descriptor model.
    /// </summary>
    public static ActivePackage FromDescriptor(ActivePackageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.ToActivePackage();
    }

    /// <summary>
    /// Converts this canonical active-package model back to the legacy descriptor model.
    /// </summary>
    public ActivePackageDescriptor ToDescriptor() => ActivePackageDescriptor.FromActivePackage(this);
}

