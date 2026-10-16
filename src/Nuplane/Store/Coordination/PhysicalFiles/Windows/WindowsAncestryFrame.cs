using Microsoft.Win32.SafeHandles;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Coordination.PhysicalFiles.Windows;

/// <summary>Retains one exact child-to-parent edge and the already-held parent ancestry.</summary>
internal sealed class WindowsAncestryFrame
{
    private readonly object _gate = new();
    private int _referenceCount = 1;
    private SafeFileHandle? _parentHandle;
    private WindowsAncestryFrame? _parentFrame;

    internal WindowsAncestryFrame(
        SafeFileHandle parentHandle,
        string component,
        PhysicalFileIdentity parentIdentity,
        PhysicalFileIdentity childIdentity,
        WindowsAncestryFrame? parentFrame)
    {
        ArgumentNullException.ThrowIfNull(parentHandle);
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(parentIdentity);
        ArgumentNullException.ThrowIfNull(childIdentity);

        parentFrame?.Retain();
        _parentHandle = parentHandle;
        _parentFrame = parentFrame;
        Component = component;
        ParentIdentity = parentIdentity;
        ChildIdentity = childIdentity;
    }

    internal string Component { get; }
    internal PhysicalFileIdentity ParentIdentity { get; }
    internal PhysicalFileIdentity ChildIdentity { get; }

    /// <summary>Retains this frame until the caller invokes <see cref="Release"/>.</summary>
    internal WindowsAncestryFrame Retain()
    {
        lock (_gate)
        {
            if (_referenceCount == 0)
            {
                throw new PackageStoreAdmissionException(
                    PackageStoreAdmissionReason.ExpiredScope,
                    "The retained directory ancestry has expired.");
            }

            checked { _referenceCount++; }
            return this;
        }
    }

    /// <summary>Gets the retained parent handle while this frame has a caller-owned reference.</summary>
    internal IntPtr GetParentHandle()
    {
        lock (_gate)
        {
            if (_referenceCount == 0 || _parentHandle is null)
            {
                throw new PackageStoreAdmissionException(
                    PackageStoreAdmissionReason.ExpiredScope,
                    "The retained parent directory handle has expired.");
            }

            return _parentHandle.DangerousGetHandle();
        }
    }

    /// <summary>Retains the next ancestry frame for a handle representing this frame's parent.</summary>
    internal WindowsAncestryFrame? RetainParentFrame()
    {
        lock (_gate)
        {
            if (_referenceCount == 0)
            {
                throw new PackageStoreAdmissionException(
                    PackageStoreAdmissionReason.ExpiredScope,
                    "The retained directory ancestry has expired.");
            }

            return _parentFrame?.Retain();
        }
    }

    /// <summary>Releases a reference owned by a directory safe handle or short provider operation.</summary>
    internal void Release()
    {
        SafeFileHandle? parentHandle = null;
        WindowsAncestryFrame? parentFrame = null;
        lock (_gate)
        {
            if (_referenceCount <= 0)
                return;

            _referenceCount--;
            if (_referenceCount == 0)
            {
                parentHandle = _parentHandle;
                parentFrame = _parentFrame;
                _parentHandle = null;
                _parentFrame = null;
            }
        }

        parentHandle?.Dispose();
        parentFrame?.Release();
    }
}
