using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Coordination;

/// <summary>Maintains one counted operation owner over an already-held physical-root ownership.</summary>
internal sealed class PackageStoreOperationState : IPackageStoreOperationOwnerControl
{
    private readonly object _gate = new();
    private readonly HashSet<PackageStoreOperationBorrow> _activeBorrows = new(ReferenceEqualityComparer.Instance);
    private readonly PhysicalRootIdentity _root;
    private readonly long _epoch;
    private readonly IAsyncDisposable _heldRootOwnership;
    private readonly IPackageStoreOperationPathValidator _pathValidator;
    private readonly PackageStoreOperationOwner _owner;

    private TaskCompletionSource<bool>? _drained;
    private TaskCompletionSource<bool>? _closeCompletion;
    private int _activeValidations;
    private bool _closing;

    internal PackageStoreOperationState(
        PhysicalRootIdentity root,
        long epoch,
        IAsyncDisposable heldRootOwnership,
        IPackageStoreOperationPathValidator pathValidator)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(heldRootOwnership);
        ArgumentNullException.ThrowIfNull(pathValidator);
        if (epoch <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(epoch), "An admitted operation epoch must be positive.");
        }

        _root = root;
        _epoch = epoch;
        _heldRootOwnership = heldRootOwnership;
        _pathValidator = pathValidator;
        _owner = new PackageStoreOperationOwner(_root, _epoch, this);
    }

    internal PackageStoreOperationOwner Owner => _owner;

    public PackageStoreOperationBorrow Borrow(PackageStoreOperationOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        lock (_gate)
        {
            EnsureOwnerMatches(owner);
            if (_closing)
            {
                throw new ObjectDisposedException(nameof(PackageStoreOperationOwner), "The package-store operation is closing.");
            }

            var borrow = new PackageStoreOperationBorrow(owner, this);
            _activeBorrows.Add(borrow);
            return borrow;
        }
    }

    public ValueTask DisposeOwnerAsync(PackageStoreOperationOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        TaskCompletionSource<bool> completion;
        Task drainTask;
        lock (_gate)
        {
            EnsureOwnerMatches(owner);
            if (_closeCompletion is not null)
            {
                return new ValueTask(_closeCompletion.Task);
            }

            _closing = true;
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _closeCompletion = completion;

            if (_activeBorrows.Count == 0 && _activeValidations == 0)
            {
                drainTask = Task.CompletedTask;
            }
            else
            {
                _drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                drainTask = _drained.Task;
            }
        }

        // Releasing the native owner may block or invoke provider code, so it always starts outside _gate.
        _ = ReleaseAfterDrainAsync(drainTask, completion);
        return new ValueTask(completion.Task);
    }

    public void ReleaseBorrow(PackageStoreOperationBorrow borrow)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        TaskCompletionSource<bool>? drained = null;
        lock (_gate)
        {
            EnsureBorrowOwnerMatches(borrow);
            if (!_activeBorrows.Remove(borrow))
            {
                throw ExpiredBorrow(borrow);
            }

            if (_closing && _activeBorrows.Count == 0 && _activeValidations == 0)
            {
                drained = _drained;
            }
        }

        drained?.TrySetResult(true);
    }

    public void ValidateForInstallPath(PackageStoreOperationBorrow borrow, string installPath)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);

        lock (_gate)
        {
            EnsureBorrowOwnerMatches(borrow);
            if (!_activeBorrows.Contains(borrow))
            {
                throw ExpiredBorrow(borrow);
            }

            _activeValidations++;
        }

        TaskCompletionSource<bool>? drained = null;
        try
        {
            // Validation is synchronous provider work; never hold _gate while invoking it.
            _pathValidator.ValidateForInstallPath(installPath);
        }
        finally
        {
            lock (_gate)
            {
                _activeValidations--;
                if (_closing && _activeBorrows.Count == 0 && _activeValidations == 0)
                {
                    drained = _drained;
                }
            }

            drained?.TrySetResult(true);
        }
    }

    private async Task ReleaseAfterDrainAsync(Task drainTask, TaskCompletionSource<bool> completion)
    {
        try
        {
            await drainTask.ConfigureAwait(false);
            await _heldRootOwnership.DisposeAsync().ConfigureAwait(false);
            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private void EnsureOwnerMatches(PackageStoreOperationOwner owner)
    {
        if (ReferenceEquals(owner, _owner))
        {
            return;
        }

        var reason = Equals(owner.Root, _root)
            ? PackageStoreAdmissionReason.ExpiredScope
            : PackageStoreAdmissionReason.RootMismatch;
        throw new PackageStoreAdmissionException(
            reason,
            "The package-store operation owner does not belong to this operation state.",
            owner.Root);
    }

    private void EnsureBorrowOwnerMatches(PackageStoreOperationBorrow borrow)
    {
        if (ReferenceEquals(borrow.Owner, _owner))
        {
            return;
        }

        var reason = Equals(borrow.Root, _root)
            ? PackageStoreAdmissionReason.ExpiredScope
            : PackageStoreAdmissionReason.RootMismatch;
        throw new PackageStoreAdmissionException(
            reason,
            "The package-store operation borrow does not belong to this operation state.",
            borrow.Root);
    }

    private PackageStoreAdmissionException ExpiredBorrow(PackageStoreOperationBorrow borrow)
        => new(
            PackageStoreAdmissionReason.ExpiredScope,
            "The package-store operation borrow has expired.",
            borrow.Root);
}
