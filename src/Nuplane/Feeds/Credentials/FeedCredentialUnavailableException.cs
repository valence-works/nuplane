namespace Nuplane.Feeds.Credentials;

/// <summary>
/// Thrown when a feed declares a secret reference that no registered
/// <see cref="ISecretReferenceProvider"/> could resolve, so the feed is refused instead of being
/// contacted without credentials.
/// </summary>
/// <remarks>
/// The message names the feed and nothing else: not the reference, not the provider it named, and
/// never a value. It travels into reconciliation failure diagnostics, which are logged and reported.
/// </remarks>
public sealed class FeedCredentialUnavailableException(string feedName)
    : InvalidOperationException($"Credentials for feed '{feedName}' could not be resolved, so the feed was refused.")
{
    /// <summary>Gets the name of the refused feed.</summary>
    public string FeedName { get; } = feedName;
}
