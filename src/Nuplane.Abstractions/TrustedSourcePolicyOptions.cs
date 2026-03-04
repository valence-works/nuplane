namespace Nuplane.Abstractions;

/// <summary>
/// Configuration options for trusted source policy enforcement, defining which desired-state
/// sources are trusted and how secret-source boundaries are enforced.
/// </summary>
public sealed class TrustedSourcePolicyOptions
{
    /// <summary>
    /// Gets or sets whether trusted source policy enforcement is enabled.
    /// When <see langword="true"/>, only sources in <see cref="TrustedSourceNames"/>
    /// may contribute desired-state requests. Defaults to <see langword="false"/>.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets the set of source names that are trusted for desired-state contributions.
    /// An empty set when <see cref="Enabled"/> is <see langword="true"/> rejects all sources.
    /// </summary>
    public HashSet<string> TrustedSourceNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets whether secret references (e.g., "secrets://...") are allowed in source configurations.
    /// Defaults to <see langword="false"/> (secrets are not allowed).
    /// </summary>
    public bool AllowSecretReferences { get; set; }

    /// <summary>
    /// Gets or sets whether inline credentials are rejected.
    /// Defaults to <see langword="true"/> (inline credentials are rejected).
    /// </summary>
    public bool RejectInlineCredentials { get; set; } = true;
}
