namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>
/// Identifies a filesystem object using native provider, volume or device, and file identity facts.
/// </summary>
/// <remarks>
/// This value is descriptive input to core validation, not proof of authority. Core must compare it
/// with identity facts obtained from held native handles before granting or using a capability.
/// </remarks>
public sealed record PhysicalFileIdentity
{
    /// <summary>
    /// Initializes an immutable native identity value.
    /// </summary>
    /// <param name="provider">The filesystem identity provider name.</param>
    /// <param name="volumeOrDeviceId">The native volume or device identifier.</param>
    /// <param name="fileId">The native file identifier.</param>
    /// <exception cref="ArgumentException">An identity component is blank.</exception>
    public PhysicalFileIdentity(string provider, string volumeOrDeviceId, string fileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeOrDeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        Provider = provider;
        VolumeOrDeviceId = volumeOrDeviceId;
        FileId = fileId;
    }

    /// <summary>Gets the name of the filesystem identity provider.</summary>
    public string Provider { get; }

    /// <summary>Gets the native volume or device identifier.</summary>
    public string VolumeOrDeviceId { get; }

    /// <summary>Gets the native file identifier.</summary>
    public string FileId { get; }
}
