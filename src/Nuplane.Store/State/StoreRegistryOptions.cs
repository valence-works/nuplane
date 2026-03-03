namespace Nuplane.Store.State;

/// <summary>
/// Configuration options for the store registry, specifying where state is persisted.
/// </summary>
public sealed class StoreRegistryOptions
{
    /// <summary>
    /// Gets or sets the file path for persisting store state, or <see langword="null"/> for in-memory only.
    /// </summary>
    public string? StateFilePath { get; set; }
}

