using Nuplane.Feeds.Setup;

namespace Nuplane.Setup;

/// <summary>
/// Exposes raw setup feed declarations to validators and setup translators.
/// </summary>
internal interface INuplaneSetupFeedDeclarationSource
{
    /// <summary>
    /// Gets the effective setup feed declarations and diagnostics.
    /// </summary>
    NuplaneFeedSetupReadResult Read();
}
