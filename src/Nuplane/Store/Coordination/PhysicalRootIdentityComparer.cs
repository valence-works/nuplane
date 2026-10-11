using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Coordination;

/// <summary>Orders physical roots by their provider and native handle identity.</summary>
internal sealed class PhysicalRootIdentityComparer : IComparer<PhysicalRootIdentity>
{
    internal static PhysicalRootIdentityComparer Instance { get; } = new();

    private PhysicalRootIdentityComparer()
    {
    }

    public int Compare(PhysicalRootIdentity? left, PhysicalRootIdentity? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;

        var result = StringComparer.Ordinal.Compare(left.HandleIdentity.Provider, right.HandleIdentity.Provider);
        if (result != 0) return result;
        result = StringComparer.Ordinal.Compare(left.HandleIdentity.VolumeOrDeviceId, right.HandleIdentity.VolumeOrDeviceId);
        return result != 0 ? result : StringComparer.Ordinal.Compare(left.HandleIdentity.FileId, right.HandleIdentity.FileId);
    }
}
