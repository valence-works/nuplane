namespace Nuplane.Abstractions;

/// <summary>
/// Describes the trust classification of a NuGet package feed.
/// </summary>
public enum FeedTrustLevel
{
    /// <summary>The feed is fully trusted; packages are accepted without additional validation.</summary>
    Trusted,
    /// <summary>The feed is partially trusted; packages require additional policy checks.</summary>
    Restricted,
    /// <summary>The feed is not trusted; packages are rejected unless an override is in place.</summary>
    Untrusted
}