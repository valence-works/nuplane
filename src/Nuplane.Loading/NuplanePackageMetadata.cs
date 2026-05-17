namespace Nuplane.Loading;

internal sealed record NuplanePackageMetadata(
    int SchemaVersion,
    NuplanePackageLoadingMetadata? Loading);

internal sealed record NuplanePackageLoadingMetadata(
    PackageLoadMode LoadMode,
    string Scope,
    string? Reason);

internal sealed record PackageMetadataLoadModeReadResult(
    bool MetadataFound,
    bool IsValid,
    NuplanePackageMetadata? Metadata,
    string? Diagnostic)
{
    public static PackageMetadataLoadModeReadResult Missing { get; } =
        new(MetadataFound: false, IsValid: false, Metadata: null, Diagnostic: null);

    public static PackageMetadataLoadModeReadResult Invalid(string diagnostic) =>
        new(MetadataFound: true, IsValid: false, Metadata: null, Diagnostic: diagnostic);

    public static PackageMetadataLoadModeReadResult Valid(NuplanePackageMetadata metadata) =>
        new(MetadataFound: true, IsValid: true, Metadata: metadata, Diagnostic: null);
}
