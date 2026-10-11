namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>Owns one admitted operation on an enrolled physical package-store root.</summary>
/// <remarks>Only Nuplane core can construct this authority-bearing handle.</remarks>
public sealed class PackageStoreOperationOwner : IAsyncDisposable
{
    private readonly IPackageStoreOperationOwnerControl _control;
    private readonly object _disposeSync = new();
    private int _disposeRequested;
    private Task? _disposeTask;

    internal PackageStoreOperationOwner(
        PhysicalRootIdentity root,
        long epoch,
        IPackageStoreOperationOwnerControl control)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(control);
        if (epoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(epoch), "An admitted operation epoch must be positive.");

        Root = root;
        Epoch = epoch;
        _control = control;
    }

    /// <summary>Gets the enrolled physical root protected by this owner.</summary>
    public PhysicalRootIdentity Root { get; }

    /// <summary>Gets the positive admission epoch assigned by Nuplane core.</summary>
    public long Epoch { get; }

    internal IPackageStoreOperationOwnerControl Control => _control;

    /// <summary>Creates a counted borrow for a nested operation that shares this admission.</summary>
    /// <exception cref="ObjectDisposedException">The owner is closing or closed.</exception>
    public PackageStoreOperationBorrow Borrow()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);
        return _control.Borrow(this);
    }

    /// <summary>Closes this owner and waits for its outstanding borrows before releasing authority.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            if (_disposeTask is null)
            {
                Interlocked.Exchange(ref _disposeRequested, 1);
                _disposeTask = _control.DisposeOwnerAsync(this).AsTask();
            }

            return new ValueTask(_disposeTask);
        }
    }
}
