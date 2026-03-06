namespace Nuplane.Abstractions;

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