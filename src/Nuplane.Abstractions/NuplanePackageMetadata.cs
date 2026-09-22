namespace Nuplane.Abstractions;

/// <summary>
/// Represents the validated contents of a package-root <c>nuplane.json</c> document, as read by
/// <c>Nuplane.Metadata.NuplanePackageMetadataReader</c>.
/// </summary>
/// <param name="SchemaVersion">The document's declared schema version (<c>1</c> or <c>2</c>).</param>
/// <param name="Loading">
/// The declared load-mode requirement, or <see langword="null"/> when the document omits it (only
/// possible in schema 2, where <c>capabilities</c> then carries the document).
/// </param>
/// <param name="Capabilities">
/// The declared capabilities. Empty when the document omits <c>capabilities</c> (only possible in
/// schema 2, where <paramref name="Loading"/> then carries the document, and always empty for
/// schema 1).
/// </param>
public sealed record NuplanePackageMetadata(
    int SchemaVersion,
    PackageLoadingMetadata? Loading,
    IReadOnlyList<PackageCapabilityDeclaration> Capabilities);
