namespace Nuplane.Loading;

/// <summary>
/// Configuration options for the Nuplane package assembly loading subsystem.
/// </summary>
public sealed class LoadingOptions
{
    /// <summary>
    /// Gets or sets whether assembly loading is enabled for reconciled packages.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the maximum time to wait for a package to deactivate before forcibly unloading.
    /// </summary>
    public TimeSpan DeactivationTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets or sets the root directory of the active package store.
    /// When set, install paths are validated to reside within this directory.
    /// </summary>
    public string? ActiveStoreRoot { get; set; }

    /// <summary>
    /// Gets or sets the default load mode used for autoloaded packages.
    /// </summary>
    public PackageLoadMode DefaultLoadMode { get; set; } = PackageLoadMode.Collectible;

    /// <summary>
    /// Gets or sets how Nuplane selects package load modes before applying the default load mode.
    /// </summary>
    public PackageLoadModeSelectionPolicy LoadModeSelectionPolicy { get; set; } = PackageLoadModeSelectionPolicy.Automatic;

    /// <summary>
    /// Gets the collection of package-specific load mode overrides.
    /// </summary>
    public ICollection<PackageLoadModeOverrideOptions> PackageLoadModes { get; } = new List<PackageLoadModeOverrideOptions>();

    /// <summary>
    /// Gets the collection of shared assembly identities whose assemblies are loaded from
    /// the host's default context rather than package-specific contexts.
    /// </summary>
    public ICollection<SharedAssemblyIdentity> SharedAssemblies { get; } = new List<SharedAssemblyIdentity>();
}
