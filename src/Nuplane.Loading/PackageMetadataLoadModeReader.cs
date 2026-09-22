using Nuplane.Metadata;

namespace Nuplane.Loading;

/// <summary>
/// Adapts the shared <see cref="NuplanePackageMetadataReader"/> to the loading module's own result
/// and diagnostics shape. The shared reader parses and validates package-root <c>nuplane.json</c>
/// (schema 1 or 2) once; this adapter maps its <c>loading</c> section into
/// <see cref="PackageMetadataLoadModeReadResult"/> exactly as the loading module always has.
/// Nuplane.Loading understands schema 1 only: a valid schema-2 document is package metadata other
/// consumers (reconciliation) can use, but it carries no load-mode decision here until this module
/// itself becomes schema-2 aware.
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
        if (metadata.SchemaVersion != 1)
        {
            return PackageMetadataLoadModeReadResult.Invalid(
                $"Package metadata for '{packageId}@{version}' uses unsupported schema version '{metadata.SchemaVersion}'.");
        }

        // A valid schema-1 document always carries loading metadata: the shared reader refuses a
        // schema-1 document without one, so this is an invariant, not a case this adapter validates.
        var loading = metadata.Loading
            ?? throw new InvalidOperationException(
                $"Shared metadata reader returned a valid schema-1 result for '{packageId}@{version}' without loading metadata.");

        // Likewise, the shared reader already restricted loading.loadMode to this module's known
        // names before returning a valid result.
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
