using System.Text.Json;
using Nuplane.Abstractions;

namespace Nuplane.Runtime.Desired;

/// <summary>
/// Reads and parses a shared desired manifest from a file path, producing a
/// <see cref="DesiredManifestReadResult"/> with deterministic validation.
/// </summary>
public sealed class DesiredManifestReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Reads the manifest from the specified file path.
    /// </summary>
    /// <param name="filePath">The path to the shared desired manifest JSON file.</param>
    /// <param name="correlationId">The correlation identifier for the current cycle.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <param name="expectedSchemaVersion">
    /// When non-null and non-empty, the manifest's <c>SchemaVersion</c> field must match this value
    /// (case-insensitive); a mismatch is treated as <see cref="ManifestReadStatus.Invalid"/>.
    /// </param>
    /// <returns>A <see cref="DesiredManifestReadResult"/> describing the outcome.</returns>
    public async Task<DesiredManifestReadResult> ReadAsync(
        string filePath,
        string correlationId,
        CancellationToken cancellationToken,
        string? expectedSchemaVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var now = DateTimeOffset.UtcNow;

        if (!File.Exists(filePath))
        {
            return new DesiredManifestReadResult(
                ManifestReadStatus.NotFound,
                ConvergenceReasonCodes.ManifestNotFound,
                filePath,
                correlationId,
                now);
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(filePath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new DesiredManifestReadResult(
                ManifestReadStatus.Unreadable,
                ConvergenceReasonCodes.ManifestUnreadable,
                filePath,
                correlationId,
                now);
        }

        ManifestJsonModel? model;
        try
        {
            model = JsonSerializer.Deserialize<ManifestJsonModel>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return new DesiredManifestReadResult(
                ManifestReadStatus.Invalid,
                ConvergenceReasonCodes.ManifestInvalid,
                filePath,
                correlationId,
                now);
        }

        if (model is null || model.Packages is null || string.IsNullOrWhiteSpace(model.SchemaVersion))
        {
            return new DesiredManifestReadResult(
                ManifestReadStatus.Invalid,
                ConvergenceReasonCodes.ManifestInvalid,
                filePath,
                correlationId,
                now);
        }

        // Enforce expected schema version when configured
        if (!string.IsNullOrWhiteSpace(expectedSchemaVersion) &&
            !string.Equals(model.SchemaVersion, expectedSchemaVersion, StringComparison.OrdinalIgnoreCase))
        {
            return new DesiredManifestReadResult(
                ManifestReadStatus.Invalid,
                ConvergenceReasonCodes.ManifestInvalid,
                filePath,
                correlationId,
                now);
        }

        // Validate: no duplicate package IDs
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pkg in model.Packages)
        {
            if (string.IsNullOrWhiteSpace(pkg.Id))
            {
                return new DesiredManifestReadResult(
                    ManifestReadStatus.Invalid,
                    ConvergenceReasonCodes.ManifestInvalid,
                    filePath,
                    correlationId,
                    now);
            }

            if (!seenIds.Add(pkg.Id))
            {
                return new DesiredManifestReadResult(
                    ManifestReadStatus.Invalid,
                    ConvergenceReasonCodes.ManifestInvalid,
                    filePath,
                    correlationId,
                    now);
            }

            if (string.IsNullOrWhiteSpace(pkg.Version))
            {
                return new DesiredManifestReadResult(
                    ManifestReadStatus.Invalid,
                    ConvergenceReasonCodes.ManifestInvalid,
                    filePath,
                    correlationId,
                    now);
            }

            // Validate: no version ranges (must be exact)
            if (pkg.Version.Contains('*') || pkg.Version.Contains('[') || pkg.Version.Contains('('))
            {
                return new DesiredManifestReadResult(
                    ManifestReadStatus.Invalid,
                    ConvergenceReasonCodes.ManifestInvalid,
                    filePath,
                    correlationId,
                    now);
            }
        }

        // Produce stable-sorted entries
        var entries = model.Packages
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Version, StringComparer.OrdinalIgnoreCase)
            .Select(p => new DesiredManifestEntry(p.Id, p.Version, p.SourceHint, p.Sha512))
            .ToList();

        var manifest = new DesiredManifest(
            model.SchemaVersion,
            model.GeneratedAtUtc,
            entries);

        return new DesiredManifestReadResult(
            ManifestReadStatus.Succeeded,
            ConvergenceReasonCodes.ManifestSucceeded,
            filePath,
            correlationId,
            now,
            manifest);
    }

    /// <summary>
    /// JSON model used for deserialization of the manifest file.
    /// </summary>
    private sealed class ManifestJsonModel
    {
        public string? SchemaVersion { get; set; }
        public DateTimeOffset GeneratedAtUtc { get; set; }
        public List<ManifestPackageJsonModel>? Packages { get; set; }
    }

    /// <summary>
    /// JSON model for a single package entry in the manifest.
    /// </summary>
    private sealed class ManifestPackageJsonModel
    {
        public string Id { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string? SourceHint { get; set; }
        public string? Sha512 { get; set; }
    }
}
