namespace Nuplane.Loading;

/// <summary>
/// Defines a shared assembly policy entry used to identify assemblies that should be loaded
/// from the host's default context instead of the package-specific context.
/// </summary>
/// <param name="Name">The simple name of the assembly (e.g., "System.Text.Json").</param>
/// <param name="PublicKeyToken">The 16-character hex public key token of the assembly.</param>
/// <param name="MajorVersion">The major version number of the assembly to match.</param>
public sealed record SharedAssemblyPolicyEntry(
    string Name,
    string PublicKeyToken,
    int MajorVersion);