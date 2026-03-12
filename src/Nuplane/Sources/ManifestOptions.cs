namespace Nuplane.Sources;

/// <summary>
/// Configuration options for the desired manifest reader.
/// </summary>
public sealed class ManifestOptions
{
    /// <summary>
    /// Gets or sets whether manifest-driven desired state is enabled.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the file path to the shared desired manifest.
    /// Required when <see cref="Enabled"/> is <see langword="true"/>.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Gets or sets the expected schema version for manifest validation.
    /// Defaults to "1.0".
    /// </summary>
    public string SchemaVersion { get; set; } = "1.0";
}