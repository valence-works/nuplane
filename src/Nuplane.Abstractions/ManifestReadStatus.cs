namespace Nuplane.Abstractions;

/// <summary>
/// Status of a desired manifest read operation.
/// </summary>
public enum ManifestReadStatus
{
    /// <summary>Manifest was read and parsed successfully.</summary>
    Succeeded,

    /// <summary>Manifest file was not found.</summary>
    NotFound,

    /// <summary>Manifest file could not be read.</summary>
    Unreadable,

    /// <summary>Manifest content is invalid.</summary>
    Invalid
}