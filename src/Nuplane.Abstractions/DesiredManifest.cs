namespace Nuplane.Abstractions;

/// <summary>
/// Represents a shared desired manifest document that defines the exact set of packages
/// expected across all replicas. The manifest is the canonical deterministic desired-state
/// document used by Phase 4 convergent runtime loading.
/// </summary>
/// <param name="SchemaVersion">The schema version of the manifest format.</param>
/// <param name="GeneratedAtUtc">The UTC timestamp when the manifest was generated.</param>
/// <param name="Packages">The ordered list of desired package entries, stable-sorted by ID then version.</param>
public sealed record DesiredManifest(
    string SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<DesiredManifestEntry> Packages);

/// <summary>
/// Represents a single package entry within a <see cref="DesiredManifest"/>.
/// </summary>
/// <param name="Id">The case-insensitive package identifier.</param>
/// <param name="Version">The exact semantic or package version (no ranges).</param>
/// <param name="SourceHint">An optional hint indicating the preferred source for acquisition.</param>
/// <param name="Sha512">An optional SHA-512 integrity hash for the package.</param>
public sealed record DesiredManifestEntry(
    string Id,
    string Version,
    string? SourceHint = null,
    string? Sha512 = null);

/// <summary>
/// Represents the result of reading and parsing a desired manifest for a single reconciliation cycle.
/// </summary>
/// <param name="Status">The outcome status of the manifest read operation.</param>
/// <param name="ReasonCode">The reason code describing the outcome.</param>
/// <param name="SourceId">The identifier of the manifest source (e.g., file path).</param>
/// <param name="CorrelationId">The correlation identifier for the current reconciliation cycle.</param>
/// <param name="ObservedAtUtc">The UTC timestamp when the manifest was observed.</param>
/// <param name="Manifest">The parsed manifest, if the read was successful; otherwise <see langword="null"/>.</param>
public sealed record DesiredManifestReadResult(
    ManifestReadStatus Status,
    string ReasonCode,
    string SourceId,
    string CorrelationId,
    DateTimeOffset ObservedAtUtc,
    DesiredManifest? Manifest = null);
