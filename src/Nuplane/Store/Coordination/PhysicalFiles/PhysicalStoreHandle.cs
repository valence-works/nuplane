using System.Runtime.InteropServices;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Coordination.PhysicalFiles;

/// <summary>Owns one provider-created native handle and scopes access to its SafeHandle.</summary>
internal abstract class PhysicalStoreHandle : IDisposable
{
    private readonly object _gate = new();
    private readonly object _creatorToken;
    private readonly SafeHandle _safeHandle;
    private bool _disposed;

    /// <summary>Initializes a native handle owned by one filesystem-provider instance.</summary>
    /// <param name="creatorToken">The provider identity required for all later handle operations.</param>
    /// <param name="safeHandle">The valid native handle whose ownership transfers to this wrapper.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="safeHandle"/> is already closed.</exception>
    /// <exception cref="ArgumentException"><paramref name="safeHandle"/> is invalid.</exception>
    protected PhysicalStoreHandle(object creatorToken, SafeHandle safeHandle)
    {
        ArgumentNullException.ThrowIfNull(creatorToken);
        ArgumentNullException.ThrowIfNull(safeHandle);
        if (safeHandle.IsClosed)
            throw new ObjectDisposedException(nameof(safeHandle));
        if (safeHandle.IsInvalid)
            throw new ArgumentException("A physical store handle must be valid.", nameof(safeHandle));

        _creatorToken = creatorToken;
        _safeHandle = safeHandle;
    }

    /// <summary>Validates provider ownership and that this wrapper remains open.</summary>
    /// <param name="creatorToken">The provider identity supplied by the caller.</param>
    /// <exception cref="PackageStoreAdmissionException">The handle belongs to another provider or has been disposed.</exception>
    internal void ValidateCreator(object creatorToken)
    {
        ArgumentNullException.ThrowIfNull(creatorToken);
        lock (_gate)
        {
            ValidateCreatorLocked(creatorToken);
        }
    }

    /// <summary>Acquires a protected SafeHandle scope for one operation by the creating provider.</summary>
    /// <param name="creatorToken">The provider identity supplied by the caller.</param>
    /// <returns>A lease that must be disposed when the native operation ends.</returns>
    /// <exception cref="PackageStoreAdmissionException">The handle belongs to another provider or has been disposed.</exception>
    internal PhysicalStoreSafeHandleLease AcquireScopedSafeHandle(object creatorToken)
    {
        ArgumentNullException.ThrowIfNull(creatorToken);
        lock (_gate)
        {
            ValidateCreatorLocked(creatorToken);
            return new PhysicalStoreSafeHandleLease(_safeHandle);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        _safeHandle.Dispose();
    }

    private void ValidateCreatorLocked(object creatorToken)
    {
        if (!ReferenceEquals(_creatorToken, creatorToken))
        {
            throw new PackageStoreAdmissionException(
                PackageStoreAdmissionReason.RootMismatch,
                "The physical store handle belongs to a different filesystem provider.");
        }

        if (_disposed)
        {
            throw new PackageStoreAdmissionException(
                PackageStoreAdmissionReason.ExpiredScope,
                "The physical store handle has been disposed.");
        }
    }

    /// <summary>Validates a provider token for cleanup after the wrapped handle was closed.</summary>
    /// <remarks>Only reservation-release cleanup may use this check on a disposed wrapper.</remarks>
    protected void ValidateCreatorForCleanup(object creatorToken)
    {
        ArgumentNullException.ThrowIfNull(creatorToken);
        if (!ReferenceEquals(_creatorToken, creatorToken))
        {
            throw new PackageStoreAdmissionException(
                PackageStoreAdmissionReason.RootMismatch,
                "The physical store handle belongs to a different filesystem provider.");
        }
    }

    /// <summary>Runs a short internal state update while provider ownership and open state are locked.</summary>
    protected TResult WithValidatedCreator<TResult>(object creatorToken, Func<TResult> action)
    {
        ArgumentNullException.ThrowIfNull(creatorToken);
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            ValidateCreatorLocked(creatorToken);
            return action();
        }
    }
}
