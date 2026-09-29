using System.Runtime.InteropServices;

namespace Nuplane.Loading;

/// <summary>
/// Identifies a shared assembly by name, public key token, and major version for the assembly sharing policy.
/// </summary>
/// <param name="Name">The simple name of the assembly.</param>
/// <param name="PublicKeyToken">
/// The 16-character hex public key token of a strong-named assembly. <see langword="null"/>, an empty string, or
/// leaving it out identifies an unsigned assembly. In configuration, <c>"PublicKeyToken": null</c> and an entry
/// with no <c>PublicKeyToken</c> key both bind as unsigned.
/// </param>
/// <param name="MajorVersion">The major version to match.</param>
public sealed record SharedAssemblyIdentity(
    string Name,
    // [Optional] rather than "= null": C# allows a default only after the last required parameter, and the
    // configuration binder constructs this record through this constructor, supplying the default for a token
    // that is null or absent instead of refusing to construct the entry at all.
    [Optional, DefaultParameterValue(null)] string? PublicKeyToken,
    int MajorVersion)
{
    private readonly string _publicKeyToken = PublicKeyToken ?? string.Empty;

    /// <summary>
    /// Gets the public key token: 16 hex characters for a strong-named assembly, or an empty string for an unsigned
    /// one. A <see langword="null"/> token is normalized to an empty string, so the two spellings of "unsigned" are
    /// the same identity.
    /// </summary>
    public string PublicKeyToken
    {
        get => _publicKeyToken;
        init => _publicKeyToken = value ?? string.Empty;
    }

    /// <summary>
    /// Deconstructs the identity into its name, its public key token (empty for an unsigned assembly) and its major
    /// version. Declared explicitly because the positional token is nullable, which would otherwise make the
    /// compiler-generated deconstruction produce a nullable token that never is one.
    /// </summary>
    /// <param name="name">The simple name of the assembly.</param>
    /// <param name="publicKeyToken">The public key token, or an empty string for an unsigned assembly.</param>
    /// <param name="majorVersion">The major version to match.</param>
    public void Deconstruct(out string name, out string publicKeyToken, out int majorVersion)
    {
        name = Name;
        publicKeyToken = PublicKeyToken;
        majorVersion = MajorVersion;
    }
}
