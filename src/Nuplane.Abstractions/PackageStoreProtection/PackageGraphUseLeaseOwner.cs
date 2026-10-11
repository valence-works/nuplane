namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>Owns release authority for a published package-graph use lease.</summary>
/// <remarks>
/// Loading may transfer release authority to a weakly observed actual context. The owner never keeps
/// the supplied context strongly and caller disposal cannot release a transferred lease early.
/// </remarks>
public sealed class PackageGraphUseLeaseOwner : IAsyncDisposable
{
    private readonly IPackageGraphUseLeaseOwnerControl _control;
    private readonly object _sync = new();
    private bool _disposeRequested;
    private bool _transferred;
    private Task? _disposeTask;

    internal PackageGraphUseLeaseOwner(
        PackageGraphUseLease lease,
        IPackageGraphUseLeaseOwnerControl control)
    {
        Lease = lease ?? throw new ArgumentNullException(nameof(lease));
        _control = control ?? throw new ArgumentNullException(nameof(control));
    }

    /// <summary>Gets the immutable protected graph view.</summary>
    public PackageGraphUseLease Lease { get; }

    /// <summary>
    /// Transfers release authority to a weakly observed reader lifetime.
    /// </summary>
    /// <param name="lifetime">The object whose actual lifetime controls release.</param>
    /// <param name="isCollectible">Whether weak death may release this lease; noncollectible lifetimes remain protected until process exit.</param>
    /// <exception cref="InvalidOperationException">The owner is disposed or was already transferred.</exception>
    public void TransferToLifetime(object lifetime, bool isCollectible)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        lock (_sync)
        {
            if (_disposeRequested || _transferred)
                throw new InvalidOperationException("Lease release authority is no longer owned by this caller.");

            var weakLifetime = new WeakReference<object>(lifetime);
            _control.TransferToLifetime(this, weakLifetime, isCollectible);
            _transferred = true;
        }
    }

    /// <summary>Releases an untransferred lease after its counted reads drain.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_transferred)
                return ValueTask.CompletedTask;
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);
            _disposeRequested = true;
            _disposeTask = _control.DisposeOwnerAsync(this).AsTask();
            return new ValueTask(_disposeTask);
        }
    }

    /// <summary>Releases a lease whose weakly observed lifetime has ended.</summary>
    /// <remarks>This is an internal callback used by Nuplane's core lifetime observer.</remarks>
    internal ValueTask DisposeTransferredAsync() => _control.DisposeTransferredOwnerAsync(this);
}
