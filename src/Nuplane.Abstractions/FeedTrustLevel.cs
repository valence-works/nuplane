namespace Nuplane.Abstractions;

/// <summary>Specifies whether a feed is trusted for package acquisition.</summary>
public enum FeedTrustLevel
{
    /// <summary>Refuses package acquisition unless an explicit package override applies.</summary>
    Untrusted,

    /// <summary>Allows package acquisition subject to the remaining resolution and integrity policies.</summary>
    Trusted,
}
