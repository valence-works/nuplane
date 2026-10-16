using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Maintains one counted operation owner over an already-held physical-root ownership.</summary>
internal sealed class PackageStoreOperationState : IPackageStoreOperationOwnerControl
{
    private readonly object _gate = new();
    private readonly HashSet<PackageStoreOperationBorrow> _activeBorrows = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PackageStoreOperationBorrow, string?> _borrowPathRestrictions = new(ReferenceEqualityComparer.Instance);
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
        => CreateBorrow(owner, allowedInstallPath: null);

    internal PackageStoreOperationBorrow BorrowForPath(PackageStoreOperationOwner owner, string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        return CreateBorrow(owner, installPath);
    }

    private PackageStoreOperationBorrow CreateBorrow(PackageStoreOperationOwner owner, string? allowedInstallPath)
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
            _borrowPathRestrictions.Add(borrow, allowedInstallPath);
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
            _borrowPathRestrictions.Remove(borrow);

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
        BeginValidation(borrow, installPath);
        try
        {
            // Validation is synchronous provider work; never hold _gate while invoking it.
            _pathValidator.ValidateForInstallPath(installPath);
        }
        finally
        {
            EndValidation();
        }
    }

    internal TResult WithValidatedPackageDirectory<TResult>(
        PackageStoreOperationBorrow borrow,
        string installPath,
        Func<IPhysicalStoreFileSystem, PhysicalStoreDirectoryHandle, TResult> callback)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        ArgumentNullException.ThrowIfNull(callback);
        BeginValidation(borrow, installPath);
        try
        {
            if (_pathValidator is not IPackageStoreOperationPackageDirectoryValidator packageDirectoryValidator)
            {
                throw new PackageStoreAdmissionException(
                    PackageStoreAdmissionReason.UnsupportedParticipant,
                    "This admitted operation cannot expose a held package directory for scoped reads.",
                    _root);
            }

            return packageDirectoryValidator.WithValidatedPackageDirectory(installPath, callback);
        }
        finally
        {
            EndValidation();
        }
    }

    internal TResult WithValidatedPackageDirectoryOrMissing<TResult>(
        PackageStoreOperationBorrow borrow,
        string installPath,
        Func<IPhysicalStoreFileSystem, PhysicalStoreDirectoryHandle?, TResult> callback)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        ArgumentNullException.ThrowIfNull(callback);
        BeginValidation(borrow, installPath);
        try
        {
            if (_pathValidator is not IPackageStoreOperationPackageDirectoryValidator packageDirectoryValidator)
            {
                throw new PackageStoreAdmissionException(
                    PackageStoreAdmissionReason.UnsupportedParticipant,
                    "This admitted operation cannot expose native package-directory or positive-absence evidence.",
                    _root);
            }

            return packageDirectoryValidator.WithValidatedPackageDirectoryOrMissing(installPath, callback);
        }
        finally
        {
            EndValidation();
        }
    }

    internal TResult WithValidatedPackageArchive<TResult>(
        PackageStoreOperationBorrow borrow,
        string installPath,
        Func<IPhysicalStoreFileSystem, PhysicalStoreDirectoryHandle, string, PhysicalStoreFileHandle, TResult> callback)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        ArgumentNullException.ThrowIfNull(callback);
        BeginValidation(borrow, installPath);
        try
        {
            if (_pathValidator is not IPackageStoreOperationPackageArchiveValidator archiveValidator)
            {
                throw new PackageStoreAdmissionException(
                    PackageStoreAdmissionReason.UnsupportedParticipant,
                    "This admitted operation cannot expose a held package archive for scoped reads.",
                    _root);
            }

            return archiveValidator.WithValidatedPackageArchive(installPath, callback);
        }
        finally
        {
            EndValidation();
        }
    }

    internal async Task<TResult> WithValidatedRootAsync<TResult>(
        PackageStoreOperationBorrow borrow,
        Func<IPhysicalStoreFileSystem, PhysicalStoreDirectoryHandle, CancellationToken, Task<TResult>> callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();
        // A path-restricted read borrow cannot expand into root-wide native access.
        BeginValidation(borrow, installPath: null);
        try
        {
            if (_heldRootOwnership is not IStoreOperationLockedMemberContext held)
            {
                throw new PackageStoreAdmissionException(
                    PackageStoreAdmissionReason.UnsupportedParticipant,
                    "This admitted operation does not expose a retained native root context.",
                    _root);
            }

            return await held.LockedMemberLocations.WithValidatedRootAsync(callback, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            EndValidation();
        }
    }

    private void BeginValidation(PackageStoreOperationBorrow borrow, string? installPath)
    {
        lock (_gate)
        {
            EnsureBorrowOwnerMatches(borrow);
            if (borrow.IsDisposed || !_activeBorrows.Contains(borrow))
                throw ExpiredBorrow(borrow);

            var allowedPath = _borrowPathRestrictions[borrow];
            if (allowedPath is not null && !string.Equals(allowedPath, installPath, StringComparison.Ordinal))
            {
                throw new PackageStoreAdmissionException(
                    PackageStoreAdmissionReason.RootMismatch,
                    "The operation borrow is restricted to its exactly admitted install path.",
                    _root);
            }

            _activeValidations++;
        }
    }

    private void EndValidation()
    {
        TaskCompletionSource<bool>? drained = null;
        lock (_gate)
        {
            _activeValidations--;
            if (_closing && _activeBorrows.Count == 0 && _activeValidations == 0)
                drained = _drained;
        }

        drained?.TrySetResult(true);
    }

    internal RootMembershipRegistry.LockedMemberLocations GetLockedMemberLocations(
        PackageStoreOperationOwner owner,
        PackageStoreOperationBorrow borrow)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(borrow);
        lock (_gate)
        {
            EnsureOwnerMatches(owner);
            EnsureBorrowOwnerMatches(borrow);
            if (borrow.IsDisposed || !_activeBorrows.Contains(borrow))
                throw ExpiredBorrow(borrow);
            if (_heldRootOwnership is not IStoreOperationLockedMemberContext held)
                throw new PackageStoreAdmissionException(
                    PackageStoreAdmissionReason.UnsupportedParticipant,
                    "This admitted operation does not expose a registry-locked member publication context.",
                    _root);
            return held.LockedMemberLocations;
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
