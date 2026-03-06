namespace Nuplane.Loading;

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