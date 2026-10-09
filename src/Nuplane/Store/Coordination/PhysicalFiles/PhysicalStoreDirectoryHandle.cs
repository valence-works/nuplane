using System.Runtime.InteropServices;

namespace Nuplane.Store.Coordination.PhysicalFiles;

/// <summary>Owns a provider-created directory handle for no-follow relative operations.</summary>
internal sealed class PhysicalStoreDirectoryHandle : PhysicalStoreHandle
{
    /// <summary>Initializes an owned directory handle created by one filesystem provider.</summary>
    /// <param name="creatorToken">The provider identity required for later use.</param>
    /// <param name="safeHandle">The valid native directory handle.</param>
    internal PhysicalStoreDirectoryHandle(object creatorToken, SafeHandle safeHandle)
        : base(creatorToken, safeHandle)
    {
    }
}
