namespace Nuplane.DirectorySource;

/// <summary>
/// Configures the optional directory-backed desired-state source for Nuplane hosts.
/// </summary>
// ReSharper disable UnusedAutoPropertyAccessor.Global
public sealed class DirectorySourceOptions
{
    /// <summary>
    /// Gets or sets the directory path that contains desired-state <c>.nupkg</c> files.
    /// </summary>
    public string DirectoryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the local directory feed that this source represents.
    /// Used as the <c>FeedName</c> on produced <see cref="Nuplane.Abstractions.PackageRequest"/> values
    /// so that resolution can target this local feed explicitly.
    /// </summary>
    public string FeedName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the logical source name emitted on produced package requests.
    /// Defaults to <see cref="FeedName"/> when not explicitly set.
    /// </summary>
    public string SourceName { get; set; } = "Directory.Drop";

    /// <summary>
    /// Gets the allowlist of package identifiers accepted by this source.
    /// Empty means no packages are accepted; add <c>"*"</c> to accept all packages explicitly.
    /// </summary>
    public IList<string> AllowlistedPackageIds { get; } = new List<string>();

    /// <summary>
    /// Gets or sets a value indicating whether file system changes trigger an immediate
    /// manual reconciliation cycle.
    /// </summary>
    public bool TriggerReconciliationOnChange { get; set; } = true;

    /// <summary>
    /// Gets or sets the debounce window used to coalesce rapid file events.
    /// </summary>
    public TimeSpan DebounceWindow { get; set; } = TimeSpan.FromSeconds(1);
}
// ReSharper restore UnusedAutoPropertyAccessor.Global
