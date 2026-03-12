namespace Nuplane.Feeds.Setup;

/// <summary>
/// Declarative translation model for a single feed in the <c>Nuplane:Setup</c> section.
/// This shape is consumed only while translating configuration into builder registrations.
/// </summary>
// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable CollectionNeverUpdated.Global
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
    /// Empty means no packages from the feed are selected.
    /// Use <see cref="IncludeAll"/> or <c>"*"</c> when unrestricted selection is intended.
    /// </summary>
    public List<string> IncludePatterns { get; set; } = [];

    /// <summary>
    /// Gets or sets directory-specific behavior when <see cref="DirectoryPath"/> is used.
    /// </summary>
    public NuplaneDirectoryFeedSetupOptions Directory { get; set; } = new();
}
// ReSharper restore CollectionNeverUpdated.Global
// ReSharper restore UnusedAutoPropertyAccessor.Global
