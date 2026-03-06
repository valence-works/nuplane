namespace Nuplane.Runtime.Configuration;

/// <summary>
/// Configuration options for optional administrative surfaces.
/// </summary>
public sealed class AdminOptions
{
    /// <summary>
    /// Gets or sets whether administrative operational surfaces are enabled.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    public bool Enabled { get; set; }
}