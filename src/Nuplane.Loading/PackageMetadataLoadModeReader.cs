using System.Text.Json;

namespace Nuplane.Loading;

internal sealed class PackageMetadataLoadModeReader
{
    internal const string MetadataFileName = "nuplane.json";
    private const long MaxMetadataBytes = 64 * 1024;
    private const int MaxReasonLength = 512;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public PackageMetadataLoadModeReadResult Read(string packageId, string version, string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);

        var metadataPath = Path.Combine(installPath, MetadataFileName);
        if (!File.Exists(metadataPath))
        {
            return PackageMetadataLoadModeReadResult.Missing;
        }

        try
        {
            using var stream = File.OpenRead(metadataPath);
            if (stream.Length > MaxMetadataBytes)
            {
                return PackageMetadataLoadModeReadResult.Invalid($"Package metadata for '{packageId}@{version}' exceeds the {MaxMetadataBytes} byte limit.");
            }

            var document = JsonSerializer.Deserialize<NuplanePackageMetadataDocument>(stream, JsonOptions);
            if (document is null)
            {
                return PackageMetadataLoadModeReadResult.Invalid($"Package metadata for '{packageId}@{version}' is empty.");
            }

            if (document.SchemaVersion != 1)
            {
                return PackageMetadataLoadModeReadResult.Invalid($"Package metadata for '{packageId}@{version}' uses unsupported schema version '{document.SchemaVersion}'.");
            }

            if (document.Loading is null)
            {
                return PackageMetadataLoadModeReadResult.Invalid($"Package metadata for '{packageId}@{version}' is missing loading metadata.");
            }

            if (string.IsNullOrWhiteSpace(document.Loading.LoadMode))
            {
                return PackageMetadataLoadModeReadResult.Invalid($"Package metadata for '{packageId}@{version}' is missing loading.loadMode.");
            }

            var loadModeName = document.Loading.LoadMode.Trim();
            if (!Enum.GetNames<PackageLoadMode>().Any(name => string.Equals(name, loadModeName, StringComparison.OrdinalIgnoreCase))
                || !Enum.TryParse<PackageLoadMode>(loadModeName, ignoreCase: true, out var loadMode))
            {
                return PackageMetadataLoadModeReadResult.Invalid($"Package metadata for '{packageId}@{version}' uses unsupported loading.loadMode '{document.Loading.LoadMode}'.");
            }

            if (string.IsNullOrWhiteSpace(document.Loading.Scope))
            {
                return PackageMetadataLoadModeReadResult.Invalid($"Package metadata for '{packageId}@{version}' is missing loading.scope.");
            }

            var scope = document.Loading.Scope.Trim();
            if (!string.Equals(scope, LoadModeScopes.DependencyClosure, StringComparison.Ordinal)
                && !string.Equals(scope, LoadModeScopes.PackageOnly, StringComparison.Ordinal))
            {
                return PackageMetadataLoadModeReadResult.Invalid($"Package metadata for '{packageId}@{version}' uses unsupported loading.scope '{document.Loading.Scope}'.");
            }

            var reason = string.IsNullOrWhiteSpace(document.Loading.Reason)
                ? null
                : document.Loading.Reason.Trim();
            if (reason?.Length > MaxReasonLength)
            {
                reason = reason[..MaxReasonLength];
            }

            return PackageMetadataLoadModeReadResult.Valid(new(
                document.SchemaVersion,
                new(loadMode, scope, reason)));
        }
        catch (JsonException ex)
        {
            return PackageMetadataLoadModeReadResult.Invalid($"Package metadata for '{packageId}@{version}' is not valid JSON: {ex.Message}");
        }
        catch (IOException ex)
        {
            return PackageMetadataLoadModeReadResult.Invalid($"Package metadata for '{packageId}@{version}' could not be read: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return PackageMetadataLoadModeReadResult.Invalid($"Package metadata for '{packageId}@{version}' could not be accessed: {ex.Message}");
        }
    }

    private sealed record NuplanePackageMetadataDocument(
        int SchemaVersion,
        NuplanePackageLoadingMetadataDocument? Loading);

    private sealed record NuplanePackageLoadingMetadataDocument(
        string? LoadMode,
        string? Scope,
        string? Reason);
}
