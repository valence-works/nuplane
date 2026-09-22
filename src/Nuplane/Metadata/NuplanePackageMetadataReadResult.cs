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
    /// <summary>The shared result for a package with no <c>nuplane.json</c> file.</summary>
    public static NuplanePackageMetadataReadResult Missing { get; } =
        new(MetadataFound: false, IsValid: false, Metadata: null, Diagnostic: null);

    /// <summary>Builds the result for a package whose <c>nuplane.json</c> file failed validation.</summary>
    /// <param name="diagnostic">The bounded, human-readable reason the file is invalid.</param>
    public static NuplanePackageMetadataReadResult Invalid(string diagnostic) =>
        new(MetadataFound: true, IsValid: false, Metadata: null, Diagnostic: diagnostic);

    /// <summary>Builds the result for a package whose <c>nuplane.json</c> file validated successfully.</summary>
    /// <param name="metadata">The validated metadata.</param>
    public static NuplanePackageMetadataReadResult Valid(NuplanePackageMetadata metadata) =>
        new(MetadataFound: true, IsValid: true, Metadata: metadata, Diagnostic: null);
}
