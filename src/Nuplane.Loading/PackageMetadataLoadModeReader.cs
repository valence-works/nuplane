using Nuplane.Metadata;

namespace Nuplane.Loading;

/// <summary>
/// Adapts the shared <see cref="NuplanePackageMetadataReader"/> to the loading module's own result
/// and diagnostics shape. The shared reader parses and validates package-root <c>nuplane.json</c>
/// (schema 1 or 2) once; this adapter maps its <c>loading</c> section into
/// <see cref="PackageMetadataLoadModeReadResult"/> using the same mapping rules regardless of
/// schema version, not a schema-version gate: a schema-2 document with a <c>loading</c> section
/// produces the identical result an equivalent schema-1 document would, and a schema-2 document
/// that omits <c>loading</c> (capabilities-only) carries no load-mode decision here and is treated
/// exactly as if the metadata file were absent — no diagnostic, nothing for a load-mode advisor to
/// act on. Schema 3+ is refused by the shared reader itself before this adapter ever sees it.
/// </summary>
internal sealed class PackageMetadataLoadModeReader
{
    internal const string MetadataFileName = NuplanePackageMetadataReader.MetadataFileName;

    private readonly NuplanePackageMetadataReader _reader = new();

    public PackageMetadataLoadModeReadResult Read(string packageId, string version, string installPath)
    {
        var result = _reader.Read(packageId, version, installPath);
        if (!result.MetadataFound)
        {
            return PackageMetadataLoadModeReadResult.Missing;
        }

        if (!result.IsValid)
        {
            return PackageMetadataLoadModeReadResult.Invalid(result.Diagnostic!);
        }

        var metadata = result.Metadata!;
        if (metadata.Loading is null)
        {
            // A valid schema-2 document can declare capabilities only. That is not a metadata
            // problem, so this is not Invalid; it simply carries no loading requirement, so this
            // module treats it exactly as if there were no metadata file at all.
            return PackageMetadataLoadModeReadResult.Missing;
        }

        var loading = metadata.Loading;

        // The shared reader already restricted loading.loadMode to this module's known names
        // before returning a valid result, for both schema versions.
        if (!Enum.TryParse<PackageLoadMode>(loading.LoadMode, ignoreCase: true, out var loadMode))
        {
            throw new InvalidOperationException(
                $"Shared metadata reader accepted loading.loadMode '{loading.LoadMode}' for '{packageId}@{version}' that Nuplane.Loading does not recognize.");
        }

        return PackageMetadataLoadModeReadResult.Valid(new(
            metadata.SchemaVersion,
            new(loadMode, loading.Scope, loading.Reason)));
    }
}
