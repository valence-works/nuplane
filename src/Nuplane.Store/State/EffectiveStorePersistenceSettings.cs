namespace Nuplane.Store.State;

/// <summary>
/// Describes the resolved persistence mode for the store registry.
/// </summary>
public enum StorePersistenceMode
{
    /// <summary>
    /// Persistence uses the default path <c>.nuplane/store-state.json</c> under the host base directory.
    /// </summary>
    DefaultPath,

    /// <summary>
    /// Persistence uses an explicitly configured file path.
    /// </summary>
    ConfiguredPath,

    /// <summary>
    /// Persistence is explicitly disabled; the store operates in memory only.
    /// </summary>
    InMemory
}

/// <summary>
/// Resolved persistence settings derived from <see cref="StoreRegistryOptions"/>.
/// This model is computed once and consumed by runtime services to determine how and where
/// store state is persisted.
/// </summary>
public sealed class EffectiveStorePersistenceSettings
{
    /// <summary>
    /// Gets the resolved persistence mode.
    /// </summary>
    public StorePersistenceMode Mode { get; }

    /// <summary>
    /// Gets the fully resolved state file path, or <see langword="null"/> when <see cref="Mode"/> is
    /// <see cref="StorePersistenceMode.InMemory"/>.
    /// </summary>
    public string? ResolvedStateFilePath { get; }

    /// <summary>
    /// Gets the originally configured state file path before normalization, or <see langword="null"/>
    /// when the path was not explicitly configured.
    /// </summary>
    public string? ConfiguredStateFilePath { get; }

    /// <summary>
    /// Gets whether the operator explicitly opted into in-memory mode.
    /// </summary>
    public bool UseInMemoryStore { get; }

    private EffectiveStorePersistenceSettings(
        StorePersistenceMode mode,
        string? resolvedStateFilePath,
        string? configuredStateFilePath,
        bool useInMemoryStore)
    {
        Mode = mode;
        ResolvedStateFilePath = resolvedStateFilePath;
        ConfiguredStateFilePath = configuredStateFilePath;
        UseInMemoryStore = useInMemoryStore;
    }

    /// <summary>
    /// Resolves effective persistence settings from the given <see cref="StoreRegistryOptions"/>.
    /// </summary>
    /// <param name="options">The store registry options to resolve.</param>
    /// <returns>A fully resolved <see cref="EffectiveStorePersistenceSettings"/>.</returns>
    public static EffectiveStorePersistenceSettings Resolve(StoreRegistryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.UseInMemoryStore)
        {
            return new EffectiveStorePersistenceSettings(
                StorePersistenceMode.InMemory,
                resolvedStateFilePath: null,
                configuredStateFilePath: options.StateFilePath,
                useInMemoryStore: true);
        }

        if (!string.IsNullOrWhiteSpace(options.StateFilePath))
        {
            return new EffectiveStorePersistenceSettings(
                StorePersistenceMode.ConfiguredPath,
                resolvedStateFilePath: Path.GetFullPath(options.StateFilePath),
                configuredStateFilePath: options.StateFilePath,
                useInMemoryStore: false);
        }

        return new EffectiveStorePersistenceSettings(
            StorePersistenceMode.DefaultPath,
            resolvedStateFilePath: Path.Combine(AppContext.BaseDirectory, ".nuplane", "store-state.json"),
            configuredStateFilePath: null,
            useInMemoryStore: false);
    }
}
