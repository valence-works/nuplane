namespace Nuplane.Feeds.Configuration;

/// <summary>Authorizes one package from one otherwise untrusted feed.</summary>
public sealed record FeedTrustOverride
{
    /// <summary>Gets or sets the exact name of the feed.</summary>
    public string FeedName { get; set; } = string.Empty;

    /// <summary>Gets or sets the exact NuGet package identifier.</summary>
    public string PackageId { get; set; } = string.Empty;

    /// <summary>Gets or sets the operator rationale recorded when the override is used.</summary>
    public string Reason { get; set; } = string.Empty;
}
