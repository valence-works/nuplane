using System.Text.Json;
using System.Text.RegularExpressions;
using NuGet.Packaging;
using NuGet.Versioning;
using Nuplane.Abstractions;

namespace Nuplane.Metadata;

/// <summary>
/// Reads and validates a package-root <c>nuplane.json</c> document (schema 1 or 2), giving every
/// consumer — reconciliation and the optional loading module alike — a single shared reading and
/// validation of the file. A document is valid or invalid as a whole; partial results are never
/// returned.
/// </summary>
public sealed class NuplanePackageMetadataReader
{
    /// <summary>The file name Nuplane reads from a resolved package's install root.</summary>
    public const string MetadataFileName = "nuplane.json";

    private const long MaxMetadataBytes = 64 * 1024;
    private const int MaxBoundedTextLength = 512;
    private const int MinSchemaVersion = 1;
    private const int MaxSchemaVersion = 2;

    // Mirrors the member names of Nuplane.Loading.PackageLoadMode (src/Nuplane.Loading.Abstractions/PackageLoadMode.cs).
    // Duplicated, rather than referenced, because core Nuplane must not depend on the optional
    // loading module: Nuplane.Loading already depends on core Nuplane, so the reference cannot run
    // the other way. Nuplane.Loading's adapter trusts this reader's validation instead of repeating
    // it, so keep this list in sync with the enum if a load mode is ever added or renamed.
    // Internal (with InternalsVisibleTo to Nuplane.Loading.Tests, see Nuplane.csproj) so drift is
    // caught by a test instead of trusted to this comment: see
    // Nuplane.Loading.Tests/NuplanePackageMetadataReaderLoadingVocabularySyncTests.cs, which asserts
    // this list equals Enum.GetNames<PackageLoadMode>() and round-trips every member through both
    // this reader and Nuplane.Loading's adapter.
    internal static readonly string[] KnownLoadModeNames = ["Collectible", "HostIntegrated"];

    // Mirrors Nuplane.Loading.LoadModeScopes (src/Nuplane.Loading/LoadModeReasonCodes.cs) for the same
    // reason, and is pinned by the same guard test (NuplanePackageMetadataReaderLoadingVocabularySyncTests).
    internal static readonly string[] KnownLoadingScopes = ["DependencyClosure", "PackageOnly"];

    // Internal (rather than private) so Nuplane.Capabilities.CapabilityOptionsValidator can apply the
    // same character rules to a host-selected option name, instead of duplicating the pattern.
    internal static readonly Regex NamePattern = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Reads and validates the <c>nuplane.json</c> document at the root of a resolved package's
    /// install path.
    /// </summary>
    /// <param name="packageId">The declaring package's id, used to identify it in diagnostics and to refuse a capability option that names it.</param>
    /// <param name="version">The declaring package's version, used only to identify it in diagnostics.</param>
    /// <param name="installPath">The package's install root directory.</param>
    /// <returns>A <see cref="NuplanePackageMetadataReadResult"/> describing whether metadata was found and, if so, whether it is valid.</returns>
    public NuplanePackageMetadataReadResult Read(string packageId, string version, string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);

        var metadataPath = Path.Combine(installPath, MetadataFileName);
        if (!File.Exists(metadataPath))
        {
            return NuplanePackageMetadataReadResult.Missing;
        }

        try
        {
            using var stream = File.OpenRead(metadataPath);
            if (stream.Length > MaxMetadataBytes)
            {
                return NuplanePackageMetadataReadResult.Invalid(
                    $"Package metadata for '{packageId}@{version}' exceeds the {MaxMetadataBytes} byte limit.");
            }

            var document = JsonSerializer.Deserialize<MetadataDocument>(stream, JsonOptions);
            if (document is null)
            {
                return NuplanePackageMetadataReadResult.Invalid($"Package metadata for '{packageId}@{version}' is empty.");
            }

            return Validate(packageId, version, document);
        }
        catch (JsonException ex)
        {
            return NuplanePackageMetadataReadResult.Invalid($"Package metadata for '{packageId}@{version}' is not valid JSON: {ex.Message}");
        }
        catch (IOException ex)
        {
            return NuplanePackageMetadataReadResult.Invalid($"Package metadata for '{packageId}@{version}' could not be read: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return NuplanePackageMetadataReadResult.Invalid($"Package metadata for '{packageId}@{version}' could not be accessed: {ex.Message}");
        }
    }

    private static NuplanePackageMetadataReadResult Validate(string packageId, string version, MetadataDocument document)
    {
        // Every refusal below reports the declared schema version, because a consumer's reaction to
        // an invalid document depends on it: only schema 2 can affect the package closure.
        if (document.SchemaVersion is < MinSchemaVersion or > MaxSchemaVersion)
        {
            return NuplanePackageMetadataReadResult.Invalid(
                $"Package metadata for '{packageId}@{version}' uses unsupported schema version '{document.SchemaVersion}'.",
                document.SchemaVersion);
        }

        // Schema 1's only section is `loading`, and it is required, exactly as today.
        if (document.SchemaVersion == 1 && document.Loading is null)
        {
            return NuplanePackageMetadataReadResult.Invalid(
                $"Package metadata for '{packageId}@{version}' is missing loading metadata.",
                document.SchemaVersion);
        }

        // Schema 2: `loading` and `capabilities` are each optional, but at least one must be present.
        var hasCapabilities = document.Capabilities is { Count: > 0 };
        if (document.SchemaVersion == 2 && document.Loading is null && !hasCapabilities)
        {
            return NuplanePackageMetadataReadResult.Invalid(
                $"Package metadata for '{packageId}@{version}' declares neither loading nor capabilities; schema 2 requires at least one.",
                document.SchemaVersion);
        }

        PackageLoadingMetadata? loading = null;
        if (document.Loading is not null)
        {
            var (validatedLoading, loadingDiagnostic) = ValidateLoading(packageId, version, document.Loading);
            if (loadingDiagnostic is not null)
            {
                return NuplanePackageMetadataReadResult.Invalid(loadingDiagnostic, document.SchemaVersion);
            }

            loading = validatedLoading;
        }

        IReadOnlyList<PackageCapabilityDeclaration> capabilities = [];
        if (hasCapabilities)
        {
            var (validatedCapabilities, capabilitiesDiagnostic) = ValidateCapabilities(packageId, version, document.Capabilities!);
            if (capabilitiesDiagnostic is not null)
            {
                return NuplanePackageMetadataReadResult.Invalid(capabilitiesDiagnostic, document.SchemaVersion);
            }

            capabilities = validatedCapabilities;
        }

        return NuplanePackageMetadataReadResult.Valid(new(document.SchemaVersion, loading, capabilities));
    }

    private static (PackageLoadingMetadata? Loading, string? Diagnostic) ValidateLoading(
        string packageId, string version, LoadingDocument loading)
    {
        if (string.IsNullOrWhiteSpace(loading.LoadMode))
        {
            return (null, $"Package metadata for '{packageId}@{version}' is missing loading.loadMode.");
        }

        var loadMode = loading.LoadMode.Trim();
        if (!KnownLoadModeNames.Any(name => string.Equals(name, loadMode, StringComparison.OrdinalIgnoreCase)))
        {
            return (null, $"Package metadata for '{packageId}@{version}' uses unsupported loading.loadMode '{loading.LoadMode}'.");
        }

        if (string.IsNullOrWhiteSpace(loading.Scope))
        {
            return (null, $"Package metadata for '{packageId}@{version}' is missing loading.scope.");
        }

        var scope = loading.Scope.Trim();
        if (!KnownLoadingScopes.Contains(scope, StringComparer.Ordinal))
        {
            return (null, $"Package metadata for '{packageId}@{version}' uses unsupported loading.scope '{loading.Scope}'.");
        }

        return (new PackageLoadingMetadata(loadMode, scope, Bound(loading.Reason)), null);
    }

    private static (IReadOnlyList<PackageCapabilityDeclaration> Capabilities, string? Diagnostic) ValidateCapabilities(
        string packageId, string version, IReadOnlyList<CapabilityDocument> capabilityDocuments)
    {
        var capabilities = new List<PackageCapabilityDeclaration>(capabilityDocuments.Count);
        var seenCapabilityNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var capabilityDocument in capabilityDocuments)
        {
            if (string.IsNullOrWhiteSpace(capabilityDocument.Name))
            {
                return ([], $"Package metadata for '{packageId}@{version}' declares a capability with a missing name.");
            }

            var name = capabilityDocument.Name.Trim();
            if (!NamePattern.IsMatch(name))
            {
                return ([], $"Package metadata for '{packageId}@{version}' declares capability '{name}' with an invalid name; " +
                    "capability and option names must be 1-64 characters of letters, digits, '.', '_', or '-'.");
            }

            if (!seenCapabilityNames.Add(name))
            {
                return ([], $"Package metadata for '{packageId}@{version}' declares capability '{name}' more than once; " +
                    "capability names must be unique, case-insensitively.");
            }

            if (capabilityDocument.Options is not { Count: > 0 })
            {
                return ([], $"Package metadata for '{packageId}@{version}' declares capability '{name}' with no options; " +
                    "every capability must declare at least one option.");
            }

            var (options, optionsDiagnostic) = ValidateOptions(packageId, version, name, capabilityDocument.Options);
            if (optionsDiagnostic is not null)
            {
                return ([], optionsDiagnostic);
            }

            capabilities.Add(new PackageCapabilityDeclaration(name, Bound(capabilityDocument.Description), options));
        }

        return (capabilities, null);
    }

    private static (IReadOnlyList<PackageCapabilityOption> Options, string? Diagnostic) ValidateOptions(
        string packageId, string version, string capabilityName, IReadOnlyList<CapabilityOptionDocument> optionDocuments)
    {
        var options = new List<PackageCapabilityOption>(optionDocuments.Count);
        var seenOptionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var optionDocument in optionDocuments)
        {
            if (string.IsNullOrWhiteSpace(optionDocument.Name))
            {
                return ([], $"Package metadata for '{packageId}@{version}' capability '{capabilityName}' declares an option with a missing name.");
            }

            var name = optionDocument.Name.Trim();
            if (!NamePattern.IsMatch(name))
            {
                return ([], $"Package metadata for '{packageId}@{version}' capability '{capabilityName}' option '{name}' has an invalid name; " +
                    "capability and option names must be 1-64 characters of letters, digits, '.', '_', or '-'.");
            }

            if (!seenOptionNames.Add(name))
            {
                return ([], $"Package metadata for '{packageId}@{version}' capability '{capabilityName}' declares option '{name}' more than once; " +
                    "option names must be unique per capability, case-insensitively.");
            }

            if (string.IsNullOrWhiteSpace(optionDocument.PackageId))
            {
                return ([], $"Package metadata for '{packageId}@{version}' capability '{capabilityName}' option '{name}' is missing packageId.");
            }

            var optionPackageId = optionDocument.PackageId.Trim();
            if (!PackageIdValidator.IsValidPackageId(optionPackageId))
            {
                return ([], $"Package metadata for '{packageId}@{version}' capability '{capabilityName}' option '{name}' has an invalid packageId " +
                    $"'{optionPackageId}'; it is not a syntactically valid NuGet package id.");
            }

            if (string.Equals(optionPackageId, packageId, StringComparison.OrdinalIgnoreCase))
            {
                return ([], $"Package metadata for '{packageId}@{version}' capability '{capabilityName}' option '{name}' declares packageId " +
                    $"'{optionPackageId}', which is the declaring package's own id; a capability option cannot name its own package.");
            }

            if (string.IsNullOrWhiteSpace(optionDocument.Version))
            {
                return ([], $"Package metadata for '{packageId}@{version}' capability '{capabilityName}' option '{name}' is missing version.");
            }

            var versionText = optionDocument.Version.Trim();
            if (!VersionRange.TryParse(versionText, out var versionRange))
            {
                return ([], $"Package metadata for '{packageId}@{version}' capability '{capabilityName}' option '{name}' has version " +
                    $"'{versionText}', which does not parse as a NuGet version range.");
            }

            if (versionRange.IsFloating)
            {
                return ([], $"Package metadata for '{packageId}@{version}' capability '{capabilityName}' option '{name}' has floating version " +
                    $"'{versionText}'; capability options must be pinned.");
            }

            // A bare version (no range syntax) is normalized to an exact single-version range
            // ("[x]"), unlike PackageDependencyGraphResolver.NormalizeDependencyVersionRange, which
            // normalizes a bare nuspec dependency version to an open floor ("[x,)"). The two differ
            // on purpose: a capability option must be pinnable to a version Elsa built against (design
            // section 5, "pinned-only restores"), so a bare version here means "exactly this version",
            // not "this version or later". An already-bracketed range passes through as authored.
            var normalizedVersionRange = versionText.StartsWith('[') || versionText.StartsWith('(')
                ? versionText
                : $"[{versionRange.MinVersion!.ToNormalizedString()}]";

            options.Add(new PackageCapabilityOption(name, optionPackageId, normalizedVersionRange));
        }

        return (options, null);
    }

    private static string? Bound(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > MaxBoundedTextLength ? trimmed[..MaxBoundedTextLength] : trimmed;
    }

    private sealed record MetadataDocument(
        int SchemaVersion,
        LoadingDocument? Loading,
        IReadOnlyList<CapabilityDocument>? Capabilities);

    private sealed record LoadingDocument(string? LoadMode, string? Scope, string? Reason);

    private sealed record CapabilityDocument(string? Name, string? Description, IReadOnlyList<CapabilityOptionDocument>? Options);

    private sealed record CapabilityOptionDocument(string? Name, string? PackageId, string? Version);
}
