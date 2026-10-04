namespace Nuplane.Sources.Configuration;

/// <summary>
/// Configuration options for resolving overlapping desired package requests from multiple sources.
/// </summary>
public sealed class DesiredStateOptions
{
    /// <summary>
    /// Gets the dictionary mapping desired-state source names to their precedence values.
    /// Lower values have higher precedence. Source names are matched case-insensitively.
    /// </summary>
    public Dictionary<string, int> SourcePriorities { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sets the precedence for the specified desired-state source.
    /// </summary>
    /// <param name="sourceName">The desired-state source name.</param>
    /// <param name="priority">The precedence value; lower values win.</param>
    public void SetPriority(string sourceName, int priority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        SourcePriorities[sourceName] = priority;
    }

    /// <summary>
    /// Gets the precedence for the specified desired-state source.
    /// </summary>
    /// <param name="sourceName">The desired-state source name.</param>
    /// <returns>The precedence value, or <see cref="int.MaxValue"/> when none is configured.</returns>
    public int GetPriority(string sourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        return SourcePriorities.GetValueOrDefault(sourceName, int.MaxValue);
    }
}
