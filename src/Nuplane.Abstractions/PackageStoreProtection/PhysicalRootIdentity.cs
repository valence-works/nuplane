namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>
/// Identifies a physical package-store root by the native identity of its held directory handle.
/// </summary>
/// <remarks>
/// The value is not a capability. Core obtains and validates root identities from native handle
/// metadata; a caller-created value cannot authorize a path or operation.
/// </remarks>
public sealed record PhysicalRootIdentity
{
    /// <summary>Initializes a physical root identity value.</summary>
    /// <param name="handleIdentity">The native identity of the root directory.</param>
    /// <exception cref="ArgumentNullException"><paramref name="handleIdentity"/> is null.</exception>
    public PhysicalRootIdentity(PhysicalFileIdentity handleIdentity)
    {
        ArgumentNullException.ThrowIfNull(handleIdentity);
        HandleIdentity = handleIdentity;
    }

    /// <summary>Gets the native identity of the root directory handle.</summary>
    public PhysicalFileIdentity HandleIdentity { get; }
}
