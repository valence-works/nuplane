using System.Runtime.InteropServices;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Coordination.PhysicalFiles;

/// <summary>Owns a provider-created file handle for no-follow relative operations.</summary>
internal sealed class PhysicalStoreFileHandle : PhysicalStoreHandle
{
    private readonly bool _createdExclusive;
    private int _initialWriteClaimed;
    private readonly object _lockReservationGate = new();
    private bool _lockReserved;
    private bool _lockPoisoned;

    /// <summary>Initializes an owned file handle created by one filesystem provider.</summary>
    /// <param name="creatorToken">The provider identity required for later use.</param>
    /// <param name="safeHandle">The valid native file handle.</param>
    /// <param name="createdExclusive">Whether this handle came from exclusive creation of a new file.</param>
    internal PhysicalStoreFileHandle(object creatorToken, SafeHandle safeHandle, bool createdExclusive)
        : base(creatorToken, safeHandle)
    {
        _createdExclusive = createdExclusive;
    }

    /// <summary>Claims the one initial write allowed on a newly and exclusively created file.</summary>
    /// <param name="creatorToken">The identity of the creating filesystem provider.</param>
    /// <exception cref="PackageStoreAdmissionException">The handle is foreign or disposed.</exception>
    /// <exception cref="InvalidOperationException">The file was opened rather than created, or its write claim was already used.</exception>
    internal void ClaimInitialWrite(object creatorToken)
    {
        WithValidatedCreator(creatorToken, () =>
        {
            if (!_createdExclusive)
                throw new InvalidOperationException("Initial control-file writes require an exclusively created file handle.");
            if (Interlocked.Exchange(ref _initialWriteClaimed, 1) != 0)
                throw new InvalidOperationException("The initial control-file write has already been claimed.");
            return true;
        });
    }

    /// <summary>Reserves this file-handle instance for at most one exclusive-lock owner.</summary>
    /// <param name="creatorToken">The identity of the filesystem provider.</param>
    /// <returns>Whether the caller acquired the reservation or must refuse this handle.</returns>
    /// <exception cref="PackageStoreAdmissionException">The handle is foreign or disposed.</exception>
    internal PhysicalStoreLockReservationResult TryReserveLock(object creatorToken)
    {
        return WithValidatedCreator(creatorToken, () =>
        {
            lock (_lockReservationGate)
            {
                if (_lockPoisoned)
                    return PhysicalStoreLockReservationResult.Poisoned;
                if (_lockReserved)
                    return PhysicalStoreLockReservationResult.Busy;

                _lockReserved = true;
                return PhysicalStoreLockReservationResult.Acquired;
            }
        });
    }

    /// <summary>Releases a reservation made by <see cref="TryReserveLock"/>.</summary>
    /// <param name="creatorToken">The identity of the filesystem provider.</param>
    /// <exception cref="PackageStoreAdmissionException">The handle belongs to another provider.</exception>
    /// <exception cref="InvalidOperationException">No lock reservation is active.</exception>
    internal void ReleaseLockReservation(object creatorToken)
    {
        ValidateCreatorForCleanup(creatorToken);
        lock (_lockReservationGate)
        {
            if (!_lockReserved)
                throw new InvalidOperationException("No per-handle lock reservation is active.");

            _lockReserved = false;
        }
    }

    /// <summary>Marks this handle unusable for future lock attempts after release could not be confirmed.</summary>
    /// <param name="creatorToken">The identity of the filesystem provider.</param>
    /// <exception cref="PackageStoreAdmissionException">The handle belongs to another provider.</exception>
    /// <exception cref="InvalidOperationException">No lock reservation is active.</exception>
    internal void MarkLockReleaseFailed(object creatorToken)
    {
        ValidateCreatorForCleanup(creatorToken);
        lock (_lockReservationGate)
        {
            if (!_lockReserved)
                throw new InvalidOperationException("No per-handle lock reservation is active.");

            _lockReserved = false;
            _lockPoisoned = true;
        }
    }
}
