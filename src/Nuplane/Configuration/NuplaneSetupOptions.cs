namespace Nuplane.Configuration;

/// <summary>
/// Declarative root configuration for the builder-only Nuplane setup surface.
/// Bind this from the <c>Nuplane:Setup</c> section.
/// </summary>
public sealed class NuplaneSetupOptions
{
    /// <summary>
    /// Gets or sets whether automatic background reconciliation is enabled.
    /// </summary>
    public bool AutomaticReconciliation { get; set; }

    /// <summary>
    /// Gets or sets the interval between automatic reconciliation cycles.
    /// Defaults to 60 seconds.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the optional store state file path.
    /// When not set, state remains in memory only.
    /// </summary>
    public string? StateFilePath { get; set; }

    /// <summary>
    /// Gets or sets the configured Nuplane feeds.
    /// </summary>
    public List<NuplaneFeedSetupOptions> Feeds { get; set; } = [];
}

