namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>Optional internal capability for controls that own resources bound to a reader lifetime.</summary>
internal interface IPackageGraphUseLeaseOwnerControlWithLifetimeCleanup
{
    void TransferToLifetime(
        PackageGraphUseLeaseOwner owner,
        WeakReference<object> lifetime,
        bool isCollectible,
        IAsyncDisposable cleanup);
}
