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
/// <param name="GraphId">The graph identity that activated the package.</param>
/// <param name="GraphGenerationId">The graph generation identity that activated the package.</param>
/// <param name="PackageRole">The package role within its graph.</param>
/// <param name="RootPackageIds">The desired root package identifiers for the graph.</param>
/// <param name="DependencyOfPackageIds">The package identifiers that depend on this package.</param>
/// <param name="Discoverable">Whether this package should be surfaced for feature discovery.</param>
public sealed record ActivePackage(
    string PackageId,
    string Version,
    string? FeedName,
    string? SourceName,
    string InstallPath,
    DateTimeOffset ActivatedAtUtc,
    string ActivationCorrelationId,
    string? GraphId = null,
    string? GraphGenerationId = null,
    ActivePackageRole PackageRole = ActivePackageRole.Root,
    IReadOnlyList<string>? RootPackageIds = null,
    IReadOnlyList<string>? DependencyOfPackageIds = null,
    bool Discoverable = true)
{
    /// <summary>
    /// Gets the graph identity that activated the package.
    /// </summary>
    public string GraphId { get; init; } = string.IsNullOrWhiteSpace(GraphId) ? PackageId : GraphId;

    /// <summary>
    /// Gets the graph generation identity that activated the package.
    /// </summary>
    public string GraphGenerationId { get; init; } = string.IsNullOrWhiteSpace(GraphGenerationId) ? ActivationCorrelationId : GraphGenerationId;

    /// <summary>
    /// Gets the desired root package identifiers for the graph.
    /// </summary>
    public IReadOnlyList<string> RootPackageIds { get; init; } = RootPackageIds ?? [PackageId];

    /// <summary>
    /// Gets the package identifiers that depend on this package.
    /// </summary>
    public IReadOnlyList<string> DependencyOfPackageIds { get; init; } = DependencyOfPackageIds ?? [];

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
