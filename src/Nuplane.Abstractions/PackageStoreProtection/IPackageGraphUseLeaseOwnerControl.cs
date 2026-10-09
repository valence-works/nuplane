namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>Internal core implementation boundary for graph-use leases and lifetime transfer.</summary>
internal interface IPackageGraphUseLeaseOwnerControl
{
    IDisposable AcquireRead(PackageGraphUseLease lease, string installPath);
    void TransferToLifetime(PackageGraphUseLeaseOwner owner, WeakReference<object> lifetime, bool isCollectible);
    ValueTask DisposeOwnerAsync(PackageGraphUseLeaseOwner owner);
    ValueTask DisposeTransferredOwnerAsync(PackageGraphUseLeaseOwner owner);
}
