namespace Nuplane.Loading.Configuration;

/// <summary>
/// Identifies a shared assembly by name, public key token, and major version for the assembly sharing policy.
/// </summary>
/// <param name="Name">The simple name of the assembly.</param>
/// <param name="PublicKeyToken">The 16-character hex public key token.</param>
/// <param name="MajorVersion">The major version to match.</param>
public sealed record SharedAssemblyIdentity(
    string Name,
    string PublicKeyToken,
    int MajorVersion);

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
    /// Gets the collection of shared assembly identities whose assemblies are loaded from
    /// the host's default context rather than package-specific contexts.
    /// </summary>
    public ICollection<SharedAssemblyIdentity> SharedAssemblies { get; } = new List<SharedAssemblyIdentity>();
}
