namespace Nuplane.Loading.Configuration;

public sealed record SharedAssemblyIdentity(
    string Name,
    string PublicKeyToken,
    int MajorVersion);

public sealed class LoadingOptions
{
    public bool Enabled { get; set; }

    public TimeSpan DeactivationTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public string? ActiveStoreRoot { get; set; }

    public ICollection<SharedAssemblyIdentity> SharedAssemblies { get; } = new List<SharedAssemblyIdentity>();

    public bool IsValid() =>
        DeactivationTimeout > TimeSpan.Zero &&
        SharedAssemblies.All(static x =>
            !string.IsNullOrWhiteSpace(x.Name) &&
            !string.IsNullOrWhiteSpace(x.PublicKeyToken) &&
            x.MajorVersion >= 0);
}
