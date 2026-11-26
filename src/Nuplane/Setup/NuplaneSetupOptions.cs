using Nuplane.Feeds.Setup;

namespace Nuplane.Setup;

/// <summary>
/// Feed declarations in the <c>Nuplane:Setup</c> section.
/// </summary>
public sealed class NuplaneSetupOptions
{
    /// <summary>
    /// Gets or sets the configured Nuplane feeds to translate into builder registrations.
    /// </summary>
    public List<NuplaneFeedSetupOptions> Feeds { get; set; } = [];
}
