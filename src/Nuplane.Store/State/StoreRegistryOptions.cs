namespace Nuplane.Store.State;

/// <summary>
/// Configuration options for the store registry, specifying where and how reconciliation state is persisted.
/// When neither <see cref="StateFilePath"/> nor <see cref="UseInMemoryStore"/> is set, the runtime defaults
/// to persisting state at <c>.nuplane/store-state.json</c> under the host application base directory.
/// </summary>
public sealed class StoreRegistryOptions
{
    /// <summary>
    /// Gets or sets the file path for persisting store state.
    /// When set, this path overrides the default <c>.nuplane/store-state.json</c> location.
    /// Cannot be combined with <see cref="UseInMemoryStore"/> set to <see langword="true"/>.
    /// </summary>
    public string? StateFilePath { get; set; }

    /// <summary>
    /// Gets or sets whether the store registry should run in explicit in-memory mode,
    /// disabling all state persistence. When <see langword="true"/>, no state file is
    /// created or read, and reconciliation state is lost on host restart.
    /// Cannot be combined with a non-empty <see cref="StateFilePath"/>.
    /// </summary>
    public bool UseInMemoryStore { get; set; }
}

