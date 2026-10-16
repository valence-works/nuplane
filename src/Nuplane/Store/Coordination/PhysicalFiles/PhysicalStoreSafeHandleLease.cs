using System.Runtime.InteropServices;

namespace Nuplane.Store.Coordination.PhysicalFiles;

/// <summary>Holds a temporary dangerous-reference scope on a physical store safe handle.</summary>
/// <remarks>The native handle is available only internally and must not outlive this lease.</remarks>
internal sealed class PhysicalStoreSafeHandleLease : IDisposable
{
    private readonly SafeHandle _handle;
    private int _disposed;

    internal PhysicalStoreSafeHandleLease(SafeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var addedReference = false;
        handle.DangerousAddRef(ref addedReference);
        if (!addedReference)
            throw new ObjectDisposedException(nameof(handle));

        _handle = handle;
    }

    /// <summary>Gets the protected raw value for one scoped native operation; the owning SafeHandle is never exposed.</summary>
    /// <exception cref="ObjectDisposedException">This lease has been disposed.</exception>
    internal IntPtr DangerousHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _handle.DangerousGetHandle();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _handle.DangerousRelease();
    }
}
