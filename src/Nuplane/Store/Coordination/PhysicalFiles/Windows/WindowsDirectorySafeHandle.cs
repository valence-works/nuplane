using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nuplane.Store.Coordination.PhysicalFiles.Windows;

/// <summary>Owns a directory handle and its ancestry until all scoped handle leases are released.</summary>
internal sealed class WindowsDirectorySafeHandle : SafeHandle
{
    private WindowsAncestryFrame? _ancestry;

    internal WindowsDirectorySafeHandle(IntPtr handle, WindowsAncestryFrame? ownedAncestry)
        : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(handle);
        _ancestry = ownedAncestry;
    }

    public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);

    internal WindowsAncestryFrame? Ancestry => Volatile.Read(ref _ancestry);

    protected override bool ReleaseHandle()
    {
        var released = WindowsNative.CloseHandle(handle);
        Interlocked.Exchange(ref _ancestry, null)?.Release();
        return released;
    }
}
