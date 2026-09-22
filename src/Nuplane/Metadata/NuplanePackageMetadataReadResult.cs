using Nuplane.Abstractions;

namespace Nuplane.Metadata;

/// <summary>
/// Represents the outcome of <see cref="NuplanePackageMetadataReader.Read"/>: metadata absent,
/// present but invalid, or present and valid.
/// </summary>
/// <param name="MetadataFound">Whether a package-root <c>nuplane.json</c> file exists.</param>
/// <param name="IsValid">Whether the file, if found, parsed and validated successfully.</param>
/// <param name="Metadata">The validated metadata, when <paramref name="IsValid"/> is <see langword="true"/>.</param>
/// <param name="Diagnostic">A bounded, human-readable validation failure reason, when found but invalid.</param>
public sealed record NuplanePackageMetadataReadResult(
    bool MetadataFound,
    bool IsValid,
    NuplanePackageMetadata? Metadata,
    string? Diagnostic)
{
    /// <summary>
    /// Gets the <c>schemaVersion</c> the document declared, or <see langword="null"/> when no
    /// document was found or it did not parse far enough to read one.
    /// </summary>
    /// <remarks>
    /// Reported even for an invalid document, which is the one case
    /// <see cref="NuplanePackageMetadata.SchemaVersion"/> cannot answer, because a consumer's
    /// reaction to invalid metadata depends on the version: a schema-1 document can only carry
    /// <c>loading</c>, which never affects the package closure, so an invalid one is ignored the way
    /// it always has been; a schema-2 document can carry <c>capabilities</c>, which does affect the
    /// closure, so an invalid one refuses its package rather than silently contributing nothing.
    /// Declared outside the primary constructor deliberately: every existing four-argument
    /// construction and deconstruction of this record keeps compiling and keeps binding.
    /// </remarks>
    public int? DeclaredSchemaVersion { get; init; }

    /// <summary>The shared result for a package with no <c>nuplane.json</c> file.</summary>
    public static NuplanePackageMetadataReadResult Missing { get; } =
        new(MetadataFound: false, IsValid: false, Metadata: null, Diagnostic: null);

    /// <summary>Builds the result for a package whose <c>nuplane.json</c> file failed validation.</summary>
    /// <param name="diagnostic">The bounded, human-readable reason the file is invalid.</param>
    /// <param name="declaredSchemaVersion">The <c>schemaVersion</c> the document declared, when it parsed far enough to carry one.</param>
    public static NuplanePackageMetadataReadResult Invalid(string diagnostic, int? declaredSchemaVersion = null) =>
        new(MetadataFound: true, IsValid: false, Metadata: null, Diagnostic: diagnostic)
        {
            DeclaredSchemaVersion = declaredSchemaVersion
        };

    /// <summary>Builds the result for a package whose <c>nuplane.json</c> file validated successfully.</summary>
    /// <param name="metadata">The validated metadata.</param>
    public static NuplanePackageMetadataReadResult Valid(NuplanePackageMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return new(MetadataFound: true, IsValid: true, Metadata: metadata, Diagnostic: null)
        {
            DeclaredSchemaVersion = metadata.SchemaVersion
        };
    }
}
