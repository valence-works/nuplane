using Nuplane.Abstractions;

namespace Nuplane.Configuration;

/// <summary>
/// Declarative configuration for a single Nuplane feed.
/// </summary>
public sealed class NuplaneFeedSetupOptions
{
    /// <summary>
    /// Gets or sets the unique feed name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the NuGet V3 service index for a remote feed.
    /// Mutually exclusive with <see cref="DirectoryPath"/>.
    /// </summary>
    public string? ServiceIndex { get; set; }

    /// <summary>
    /// Gets or sets the local directory path for a file-backed feed.
    /// Mutually exclusive with <see cref="ServiceIndex"/>.
    /// </summary>
    public string? DirectoryPath { get; set; }

    /// <summary>
    /// Gets or sets the trust level for the feed.
    /// </summary>
    public FeedTrustLevel TrustLevel { get; set; } = FeedTrustLevel.Trusted;

    /// <summary>
    /// Gets or sets the optional credential reference for authenticated feeds.
    /// </summary>
    public string? Credentials { get; set; }

    /// <summary>
    /// Gets or sets whether all packages from this feed should be accepted.
    /// Prefer this or <c>"*"</c> in <see cref="IncludePatterns"/> when unrestricted feed scope
    /// should be explicit in configuration.
    /// </summary>
    public bool IncludeAll { get; set; }

    /// <summary>
    /// Gets or sets the include patterns applied to this feed.
    /// Empty still means all packages from the feed are accepted for backward compatibility,
    /// but prefer <see cref="IncludeAll"/> or <c>"*"</c> when that intent should be explicit.
    /// </summary>
    public List<string> IncludePatterns { get; set; } = [];

    /// <summary>
    /// Gets or sets directory-specific behavior when <see cref="DirectoryPath"/> is used.
    /// </summary>
    public NuplaneDirectoryFeedSetupOptions Directory { get; set; } = new();
}
