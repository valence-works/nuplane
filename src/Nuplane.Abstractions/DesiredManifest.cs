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