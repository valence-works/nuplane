using System.Text;
using System.Text.Json;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Sources;

/// <summary>
/// Reads and parses a shared desired manifest from a file path, producing a
/// <see cref="DesiredManifestReadResult"/> with deterministic validation.
/// </summary>
public sealed class DesiredManifestReader
{
    private readonly Func<CancellationToken, Task>? _afterNativeReadAsync;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Initializes a new instance of <see cref="DesiredManifestReader"/>.</summary>
    public DesiredManifestReader()
    {
    }

    internal DesiredManifestReader(Func<CancellationToken, Task> afterNativeReadAsync)
        => _afterNativeReadAsync = afterNativeReadAsync ?? throw new ArgumentNullException(nameof(afterNativeReadAsync));

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
        => await ReadCoreAsync(filePath, correlationId, cancellationToken, borrow: null, expectedSchemaVersion)
            .ConfigureAwait(false);

    /// <summary>Reads the manifest while retaining the caller's exact package-store operation.</summary>
    /// <param name="borrow">The original counted borrow owned by the caller.</param>
    /// <param name="filePath">The configured manifest locator, preserved in the result.</param>
    /// <param name="correlationId">The cycle correlation identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <param name="expectedSchemaVersion">An optional required schema version.</param>
    public Task<DesiredManifestReadResult> ReadAsync(
        PackageStoreOperationBorrow borrow,
        string filePath,
        string correlationId,
        CancellationToken cancellationToken,
        string? expectedSchemaVersion = null)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        return ReadCoreAsync(filePath, correlationId, cancellationToken, borrow, expectedSchemaVersion);
    }

    private async Task<DesiredManifestReadResult> ReadCoreAsync(
        string filePath,
        string correlationId,
        CancellationToken cancellationToken,
        PackageStoreOperationBorrow? borrow,
        string? expectedSchemaVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        var (parentPath, basename) = SplitParentAndBasename(filePath);
        return await DesiredSourceDirectoryAccess.WithDirectoryAsync(
            parentPath,
            borrow,
            async session =>
            {
                if (basename is null || session.IsMissing)
                {
                    return NotFound(filePath, correlationId, now);
                }

                var read = await session.ReadDesiredSourceTextAsync(
                    basename,
                    cancellationToken,
                    _afterNativeReadAsync).ConfigureAwait(false);
                string json;
                switch (read.Kind)
                {
                    case PhysicalStoreDesiredSourceTextReadKind.Missing:
                    case PhysicalStoreDesiredSourceTextReadKind.Directory:
                        return NotFound(filePath, correlationId, now);
                    case PhysicalStoreDesiredSourceTextReadKind.Unreadable:
                        return new DesiredManifestReadResult(
                            ManifestReadStatus.Unreadable,
                            ConvergenceReasonCodes.ManifestUnreadable,
                            filePath,
                            correlationId,
                            now);
                    case PhysicalStoreDesiredSourceTextReadKind.Readable:
                        if (read.Content is null)
                            throw new PackageStoreAdmissionException(
                                PackageStoreAdmissionReason.UnknownAuthority,
                                "The native desired-source reader returned no bytes for a readable file.");
                        using (var bytes = new MemoryStream(read.Content, writable: false))
                        using (var text = new StreamReader(bytes, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                        {
                            json = await text.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                        }

                        session.Revalidate();
                        return ParseJson(json, filePath, correlationId, now, expectedSchemaVersion);
                    default:
                        throw new PackageStoreAdmissionException(
                            PackageStoreAdmissionReason.UnknownAuthority,
                            "The native desired-source reader returned an unknown result kind.");
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static DesiredManifestReadResult NotFound(string filePath, string correlationId, DateTimeOffset now)
        => new(
            ManifestReadStatus.NotFound,
            ConvergenceReasonCodes.ManifestNotFound,
            filePath,
            correlationId,
            now);

    private static (string ParentPath, string? Basename) SplitParentAndBasename(string filePath)
    {
        var separator = Math.Max(
            filePath.LastIndexOf(Path.DirectorySeparatorChar),
            filePath.LastIndexOf(Path.AltDirectorySeparatorChar));
        var basename = separator < 0 ? filePath : filePath[(separator + 1)..];
        if (basename.Length == 0)
            return (filePath, null);

        string parentPath;
        if (separator < 0)
        {
            parentPath = ".";
        }
        else if (separator == 0)
        {
            parentPath = filePath[..1];
        }
        else if (OperatingSystem.IsWindows() && separator == 2 && filePath.Length >= 3 && filePath[1] == ':')
        {
            parentPath = filePath[..3];
        }
        else
        {
            parentPath = filePath[..separator];
        }

        return (parentPath, basename);
    }

    private static DesiredManifestReadResult ParseJson(
        string json,
        string filePath,
        string correlationId,
        DateTimeOffset now,
        string? expectedSchemaVersion)
    {

        ManifestJsonModel? model;
        try
        {
            model = JsonSerializer.Deserialize<ManifestJsonModel>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return new(
                ManifestReadStatus.Invalid,
                ConvergenceReasonCodes.ManifestInvalid,
                filePath,
                correlationId,
                now);
        }

        if (model is null || model.Packages is null || string.IsNullOrWhiteSpace(model.SchemaVersion))
        {
            return new(
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
            return new(
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
                return new(
                    ManifestReadStatus.Invalid,
                    ConvergenceReasonCodes.ManifestInvalid,
                    filePath,
                    correlationId,
                    now);
            }

            if (!seenIds.Add(pkg.Id))
            {
                return new(
                    ManifestReadStatus.Invalid,
                    ConvergenceReasonCodes.ManifestInvalid,
                    filePath,
                    correlationId,
                    now);
            }

            if (string.IsNullOrWhiteSpace(pkg.Version))
            {
                return new(
                    ManifestReadStatus.Invalid,
                    ConvergenceReasonCodes.ManifestInvalid,
                    filePath,
                    correlationId,
                    now);
            }

            // Validate: no version ranges (must be exact)
            if (pkg.Version.Contains('*') || pkg.Version.Contains('[') || pkg.Version.Contains('('))
            {
                return new(
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

        return new(
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
