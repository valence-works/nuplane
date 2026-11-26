namespace Nuplane.Sources.Directory;

/// <summary>Settings for a directory feed's background watcher.</summary>
internal sealed class DirectorySourceOptions
{
    public string DirectoryPath { get; init; } = string.Empty;

    public string FeedName { get; init; } = string.Empty;

    public TimeSpan DebounceWindow { get; init; } = TimeSpan.FromSeconds(1);
}
