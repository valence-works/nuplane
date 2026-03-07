namespace Nuplane.Configuration;

/// <summary>
/// Declarative translation model for the <c>Nuplane:Setup</c> section.
/// This shape exists to map configuration onto the fluent builder surface; runtime services consume
/// the dedicated option types that the builder ultimately configures.
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
    /// Gets or sets the configured Nuplane feeds to translate into builder registrations.
    /// </summary>
    public List<NuplaneFeedSetupOptions> Feeds { get; set; } = [];
}
