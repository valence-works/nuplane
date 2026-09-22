namespace Nuplane.Abstractions;

/// <summary>
/// Represents the package-declared load-mode requirement from the <c>loading</c> section of a
/// package-root <c>nuplane.json</c> document.
/// </summary>
/// <param name="LoadMode">
/// The declared load mode name (e.g. <c>HostIntegrated</c> or <c>Collectible</c>), validated against
/// the known load-mode vocabulary by <c>Nuplane.Metadata.NuplanePackageMetadataReader</c>.
/// </param>
/// <param name="Scope">
/// The declared promotion scope (e.g. <c>DependencyClosure</c> or <c>PackageOnly</c>).
/// </param>
/// <param name="Reason">An optional bounded human-readable explanation for the requirement.</param>
public sealed record PackageLoadingMetadata(
    string LoadMode,
    string Scope,
    string? Reason);
