using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Windows;

namespace Nuplane.Store.Coordination;

/// <summary>Performs bounded metadata and control-file operations relative to held Windows directory handles.</summary>
/// <remarks>
/// The initial provider boundary is Windows x64 on local NTFS volumes. Child operations use one exact component
/// relative to an already held directory handle. Its separate publication companion moves/removes only single
/// control files; the provider does not delete package trees or resolve paths from remembered absolute names.
/// </remarks>
internal sealed partial class WindowsPhysicalStoreFileSystem : IPhysicalStoreFileSystem, IPhysicalStoreNameFileSystem
{
    private static readonly Encoding StrictUnicode = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private readonly object _providerToken = new();
    private readonly ConditionalWeakTable<PhysicalStoreHandle, WindowsHandleState> _states = new();

    internal static bool IsSupportedPlatform
        => OperatingSystem.IsWindows() &&
           RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
           RuntimeInformation.OSArchitecture == Architecture.X64;

    /// <inheritdoc />
    public PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor)
    {
        RequireSupportedPlatform();
        ValidateNamespaceAnchor(anchor);

        SafeFileHandle? opened = null;
        try
        {
            opened = WindowsNative.OpenNamespaceRoot(anchor);
            var raw = opened.DangerousGetHandle();
            var info = QueryEntry(raw, "inspect the namespace root");
            RequireKind(info, PhysicalStoreEntryKind.Directory, "The namespace anchor is not a directory root.");
            RequireLocalDisk(WindowsNative.QueryDeviceInfo(raw));
            var fileSystem = WindowsNative.GetFileSystemName(raw);
            if (!string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                throw Refusal(
                    PackageStoreAdmissionReason.UnsupportedFilesystem,
                    $"The Windows physical store provider supports local NTFS roots only; this root reports '{fileSystem}'.");
            }

            if (anchor.Length == 3)
            {
                var volumeRoot = WindowsNative.GetVolumeGuidRoot(anchor);
                ValidateNamespaceAnchor(volumeRoot);
                using var physicalVolumeRoot = WindowsNative.OpenNamespaceRoot(volumeRoot);
                var volumeInfo = QueryEntry(physicalVolumeRoot.DangerousGetHandle(), "inspect the physical volume root");
                RequireLocalDisk(WindowsNative.QueryDeviceInfo(physicalVolumeRoot.DangerousGetHandle()));
                if (!string.Equals(WindowsNative.GetFileSystemName(physicalVolumeRoot.DangerousGetHandle()), "NTFS", StringComparison.OrdinalIgnoreCase) ||
                    volumeInfo.Identity != info.Identity)
                {
                    throw Refusal(
                        PackageStoreAdmissionReason.UnsupportedFilesystem,
                        "The drive spelling is redirected or does not identify the physical volume root.");
                }
            }

            var directory = OwnDirectory(opened, info.Identity, ancestry: null);
            opened = null;
            return directory;
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("open namespace root", exception);
        }
        finally
        {
            opened?.Dispose();
        }
    }

    /// <inheritdoc />
    public PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ValidateName(singleName);
        RequireSupportedPlatform();

        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var parentState = GetState(parent);
        using var entry = OpenChildForInspection(parentLease.DangerousHandle, singleName);
        if (entry is null)
            return null;

        var info = QueryEntry(entry.DangerousGetHandle(), "inspect a child without following its final reparse point");
        RequireSameVolume(parentState.Identity, info.Identity);
        return info;
    }

    /// <inheritdoc />
    public PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ValidateName(singleName);
        RequireSupportedPlatform();

        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var parentState = GetState(parent);
        var parentInfo = QueryEntry(parentLease.DangerousHandle, "inspect the held parent directory");
        RequireKind(parentInfo, PhysicalStoreEntryKind.Directory, "The held parent is not a directory.");
        if (parentInfo.Identity != parentState.Identity)
            throw Unknown("The held parent directory identity changed.");

        var parentFrame = parentState.Ancestry?.Retain();
        SafeFileHandle? childHandle = null;
        SafeFileHandle? parentDuplicate = null;
        WindowsAncestryFrame? childFrame = null;
        try
        {
            childHandle = WindowsNative.OpenRelative(
                parentLease.DangerousHandle,
                singleName,
                WindowsNative.FileReadAttributes | WindowsNative.FileListDirectory | WindowsNative.Synchronize,
                WindowsNative.FileOpen,
                WindowsNative.FileDirectoryFile);

            var childInfo = QueryEntry(childHandle.DangerousGetHandle(), "inspect the opened child directory");
            RequireKind(childInfo, PhysicalStoreEntryKind.Directory, "The requested child is not a non-link directory.");
            RequireSameVolume(parentInfo.Identity, childInfo.Identity);

            parentDuplicate = WindowsNative.Duplicate(parentLease.DangerousHandle);
            childFrame = new WindowsAncestryFrame(
                parentDuplicate,
                singleName,
                parentInfo.Identity,
                childInfo.Identity,
                parentFrame);
            parentDuplicate = null;

            var frameToTransfer = childFrame;
            childFrame = null;
            var wrapper = OwnDirectory(childHandle, childInfo.Identity, frameToTransfer);
            childHandle = null;
            return wrapper;
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("open a child directory without following its final reparse point", exception);
        }
        finally
        {
            childHandle?.Dispose();
            parentDuplicate?.Dispose();
            childFrame?.Release();
            parentFrame?.Release();
        }
    }

    /// <inheritdoc />
    public PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        RequireSupportedPlatform();

        using var childLease = directory.AcquireScopedSafeHandle(_providerToken);
        var state = GetState(directory);
        var childInfo = QueryEntry(childLease.DangerousHandle, "inspect the held directory");
        RequireKind(childInfo, PhysicalStoreEntryKind.Directory, "The held object is not a directory.");
        if (childInfo.Identity != state.Identity)
            throw Unknown("The held directory identity changed.");

        var edge = state.Ancestry?.Retain();
        if (edge is null)
        {
            try
            {
                var rootCopy = WindowsNative.Duplicate(childLease.DangerousHandle);
                return OwnDirectory(rootCopy, childInfo.Identity, ancestry: null);
            }
            catch (WindowsNativeCallException exception)
            {
                throw NativeFailure("duplicate the held namespace root", exception);
            }
        }

        try
        {
            if (edge.ChildIdentity != childInfo.Identity)
                throw Unknown("The retained child identity no longer matches its ancestry edge.");

            using (var recheckedChild = WindowsNative.OpenRelative(
                       edge.GetParentHandle(),
                       edge.Component,
                       WindowsNative.FileReadAttributes | WindowsNative.FileListDirectory | WindowsNative.Synchronize,
                       WindowsNative.FileOpen,
                       WindowsNative.FileDirectoryFile))
            {
                var recheckedInfo = QueryEntry(recheckedChild.DangerousGetHandle(), "recheck the child-to-parent edge");
                if (recheckedInfo.Kind != PhysicalStoreEntryKind.Directory || recheckedInfo.Identity != edge.ChildIdentity)
                    throw Unknown("The child-to-parent ancestry edge was replaced or moved.");
            }

            SafeFileHandle? parentCopy = null;
            WindowsAncestryFrame? parentFrame = null;
            try
            {
                parentCopy = WindowsNative.Duplicate(edge.GetParentHandle());
                parentFrame = edge.RetainParentFrame();
                var frameToTransfer = parentFrame;
                var handleToTransfer = parentCopy;
                parentCopy = null;
                parentFrame = null;
                return OwnDirectory(handleToTransfer, edge.ParentIdentity, frameToTransfer);
            }
            finally
            {
                parentCopy?.Dispose();
                parentFrame?.Release();
            }
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("revalidate the held directory parent edge", exception);
        }
        finally
        {
            edge.Release();
        }
    }

    /// <inheritdoc />
    public PhysicalStoreFileHandle OpenFileChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, FileAccess access)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ValidateName(singleName);
        RequireSupportedPlatform();
        var desiredAccess = AccessFor(access) | WindowsNative.FileReadAttributes | WindowsNative.Synchronize;

        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var parentState = GetState(parent);
        SafeFileHandle? opened = null;
        try
        {
            opened = WindowsNative.OpenRelative(
                parentLease.DangerousHandle,
                singleName,
                desiredAccess,
                WindowsNative.FileOpen,
                WindowsNative.FileNonDirectoryFile);
            var info = QueryEntry(opened.DangerousGetHandle(), "inspect an opened control file");
            RequireControlFile(info, "The requested child is not a single-link regular file.");
            RequireSameVolume(parentState.Identity, info.Identity);
            var wrapper = OwnFile(opened, createdExclusive: false, info.Identity);
            opened = null;
            return wrapper;
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("open a child file without following its final reparse point", exception);
        }
        finally
        {
            opened?.Dispose();
        }
    }

    /// <inheritdoc />
    public string ReadLinkTargetNoFollow(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedLinkIdentity)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(expectedLinkIdentity);
        ValidateName(singleName);
        RequireSupportedPlatform();

        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var parentState = GetState(parent);
        SafeFileHandle? link = null;
        try
        {
            link = WindowsNative.OpenRelative(
                parentLease.DangerousHandle,
                singleName,
                WindowsNative.FileReadAttributes | WindowsNative.Synchronize,
                WindowsNative.FileOpen,
                createOptions: 0);
            var before = QueryEntry(link.DangerousGetHandle(), "inspect a held reparse entry");
            RequireSameVolume(parentState.Identity, before.Identity);
            if (before.Kind != PhysicalStoreEntryKind.SymbolicLink || before.Identity != expectedLinkIdentity)
                throw Unknown("The no-follow reparse entry does not match the expected identity.");

            var target = ParseReparseTarget(WindowsNative.ReadReparseData(link.DangerousGetHandle()));
            var after = QueryEntry(link.DangerousGetHandle(), "recheck a held reparse entry");
            if (after.Kind != PhysicalStoreEntryKind.SymbolicLink || after.Identity != expectedLinkIdentity)
                throw Unknown("The reparse identity changed while its target was read.");
            return target;
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("read a no-follow reparse target", exception);
        }
        finally
        {
            link?.Dispose();
        }
    }

    /// <inheritdoc />
    public PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        using var lease = handle.AcquireScopedSafeHandle(_providerToken);
        RequireSupportedPlatform();
        var state = GetState(handle);
        try
        {
            var info = QueryEntry(lease.DangerousHandle, "inspect a held native handle");
            if (info.Identity != state.Identity)
                throw Unknown("The held handle's physical identity changed.");
            return info;
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("inspect a held native handle", exception);
        }
    }

    /// <inheritdoc />
    public PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ValidateName(singleName);
        RequireSupportedPlatform();

        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var parentState = GetState(parent);
        var parentInfo = QueryEntry(parentLease.DangerousHandle, "inspect the held parent directory");
        if (parentInfo.Identity != parentState.Identity || parentInfo.Kind != PhysicalStoreEntryKind.Directory)
            throw Unknown("The held parent directory identity changed.");

        SafeFileHandle? created = null;
        SafeFileHandle? parentDuplicate = null;
        WindowsAncestryFrame? parentFrame = parentState.Ancestry?.Retain();
        WindowsAncestryFrame? childFrame = null;
        try
        {
            created = WindowsNative.OpenRelative(
                parentLease.DangerousHandle,
                singleName,
                WindowsNative.FileReadAttributes | WindowsNative.FileListDirectory | WindowsNative.Synchronize,
                WindowsNative.FileCreate,
                WindowsNative.FileDirectoryFile,
                WindowsNative.FileAttributeDirectory);
            var info = QueryEntry(created.DangerousGetHandle(), "inspect a newly created directory");
            RequireKind(info, PhysicalStoreEntryKind.Directory, "The exclusive directory creation returned a non-directory or reparse point.");
            RequireSameVolume(parentInfo.Identity, info.Identity);

            parentDuplicate = WindowsNative.Duplicate(parentLease.DangerousHandle);
            childFrame = new WindowsAncestryFrame(parentDuplicate, singleName, parentInfo.Identity, info.Identity, parentFrame);
            parentDuplicate = null;

            var frameToTransfer = childFrame;
            childFrame = null;
            var wrapper = OwnDirectory(created, info.Identity, frameToTransfer);
            created = null;
            return wrapper;
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("create a child directory exclusively", exception);
        }
        finally
        {
            created?.Dispose();
            parentDuplicate?.Dispose();
            childFrame?.Release();
            parentFrame?.Release();
        }
    }

    /// <inheritdoc />
    public PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ValidateName(singleName);
        RequireSupportedPlatform();

        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var parentState = GetState(parent);
        SafeFileHandle? created = null;
        try
        {
            created = WindowsNative.OpenRelative(
                parentLease.DangerousHandle,
                singleName,
                WindowsNative.FileReadAttributes | WindowsNative.FileReadData | WindowsNative.FileWriteData | WindowsNative.Synchronize,
                WindowsNative.FileCreate,
                WindowsNative.FileNonDirectoryFile,
                WindowsNative.FileAttributeNormal);
            var info = QueryEntry(created.DangerousGetHandle(), "inspect a newly created control file");
            RequireControlFile(info, "Exclusive control-file creation returned a linked or non-regular file.");
            RequireSameVolume(parentState.Identity, info.Identity);
            if (info.Length != 0)
                throw Unknown("An exclusively created control file was not empty.");

            var result = OwnFile(created, createdExclusive: true, info.Identity);
            created = null;
            return result;
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("create a control file exclusively", exception);
        }
        finally
        {
            created?.Dispose();
        }
    }

    /// <inheritdoc />
    public byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (maximumBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        RequireSupportedPlatform();

        using var lease = file.AcquireScopedSafeHandle(_providerToken);
        var state = GetState(file);
        try
        {
            var before = QueryEntry(lease.DangerousHandle, "inspect a control file before reading");
            RequireControlFile(before, "The held control file is linked or not regular.");
            if (before.Identity != state.Identity || before.Length > maximumBytes)
                throw Unknown("The control file identity or length is outside the requested read bound.");

            var bytes = ReadExact(lease.DangerousHandle, checked((int)before.Length));
            var after = QueryEntry(lease.DangerousHandle, "recheck a control file after reading");
            RequireControlFile(after, "The control file changed while it was read.");
            if (after.Identity != before.Identity || after.Length != before.Length)
                throw Unknown("The control file changed while its bounded bytes were read.");
            return bytes;
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("read a bounded control file", exception);
        }
    }

    /// <inheritdoc />
    public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
    {
        ArgumentNullException.ThrowIfNull(file);
        RequireSupportedPlatform();
        file.ClaimInitialWrite(_providerToken);

        using var lease = file.AcquireScopedSafeHandle(_providerToken);
        var state = GetState(file);
        try
        {
            var before = QueryEntry(lease.DangerousHandle, "inspect a new control file before writing");
            RequireControlFile(before, "The new control file is linked or not regular.");
            if (before.Identity != state.Identity || before.Length != 0)
                throw Unknown("The new control file changed before its one-time write.");

            var offset = 0;
            while (offset < contents.Length)
            {
                var written = WindowsNative.WriteAt(lease.DangerousHandle, contents[offset..], offset);
                if (written <= 0)
                    throw Unknown("The control-file write made no progress.");
                offset += written;
            }

            WindowsNative.Flush(lease.DangerousHandle);
            var after = QueryEntry(lease.DangerousHandle, "recheck a control file after writing");
            RequireControlFile(after, "The new control file changed while being written.");
            if (after.Identity != before.Identity || after.Length != contents.Length)
                throw Unknown("The new control file did not retain its identity and expected length.");
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("write and flush a new control file", exception);
        }
    }

    /// <inheritdoc />
    public ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
    {
        ArgumentNullException.ThrowIfNull(file);
        RequireSupportedPlatform();
        var reservation = file.TryReserveLock(_providerToken);
        if (reservation == PhysicalStoreLockReservationResult.Busy)
            return ValueTask.FromResult<IAsyncDisposable?>(null);
        if (reservation == PhysicalStoreLockReservationResult.Poisoned)
            throw Unknown("A previous unlock failed; this file handle cannot establish lock availability.");

        PhysicalStoreSafeHandleLease? handleLease = null;
        var reservationTransferred = false;
        try
        {
            var state = GetState(file);
            handleLease = file.AcquireScopedSafeHandle(_providerToken);
            var info = QueryEntry(handleLease.DangerousHandle, "inspect a lock file before locking");
            RequireControlFile(info, "The lock file is linked or not regular.");
            if (info.Identity != state.Identity)
                throw Unknown("The lock-file identity changed before locking.");

            var overlapped = default(WindowsNative.Overlapped);
            if (!WindowsNative.TryLock(handleLease.DangerousHandle, ref overlapped))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == WindowsNative.ErrorLockViolation)
                    return ValueTask.FromResult<IAsyncDisposable?>(null);
                throw NativeError("acquire a nonblocking exclusive lock", error);
            }

            var owner = new WindowsFileLockOwner(_providerToken, file, handleLease, overlapped);
            handleLease = null;
            reservationTransferred = true;
            return ValueTask.FromResult<IAsyncDisposable?>(owner);
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("acquire a nonblocking exclusive lock", exception);
        }
        finally
        {
            handleLease?.Dispose();
            if (!reservationTransferred)
                file.ReleaseLockReservation(_providerToken);
        }
    }

    private static byte[] ReadExact(IntPtr handle, int length)
    {
        var bytes = new byte[length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var chunk = WindowsNative.ReadAt(handle, offset, bytes.Length - offset);
            if (chunk.Length == 0)
                throw Unknown("The control file ended before its observed length.");
            chunk.CopyTo(bytes, offset);
            offset += chunk.Length;
        }

        return bytes;
    }

    private PhysicalStoreDirectoryHandle OwnDirectory(SafeFileHandle handle, PhysicalFileIdentity identity, WindowsAncestryFrame? ancestry)
    {
        var raw = TakeRawHandle(handle);
        WindowsDirectorySafeHandle? safe = null;
        PhysicalStoreDirectoryHandle? wrapper = null;
        try
        {
            safe = new WindowsDirectorySafeHandle(raw, ancestry);
            ancestry = null;
            wrapper = new PhysicalStoreDirectoryHandle(_providerToken, safe);
            _states.Add(wrapper, new WindowsHandleState(identity, safe.Ancestry));
            var result = wrapper;
            wrapper = null;
            safe = null;
            return result;
        }
        catch
        {
            wrapper?.Dispose();
            if (safe is not null)
                safe.Dispose();
            else if (raw != IntPtr.Zero && raw != new IntPtr(-1))
                _ = WindowsNative.CloseHandle(raw);
            ancestry?.Release();
            throw;
        }
    }

    private PhysicalStoreFileHandle OwnFile(SafeFileHandle handle, bool createdExclusive, PhysicalFileIdentity identity)
    {
        var wrapper = new PhysicalStoreFileHandle(_providerToken, handle, createdExclusive);
        try
        {
            _states.Add(wrapper, new WindowsHandleState(identity, Ancestry: null));
            return wrapper;
        }
        catch
        {
            wrapper.Dispose();
            throw;
        }
    }

    private SafeFileHandle? OpenChildForInspection(IntPtr parent, string name)
    {
        try
        {
            return WindowsNative.OpenRelative(
                parent,
                name,
                WindowsNative.FileReadAttributes | WindowsNative.Synchronize,
                WindowsNative.FileOpen,
                createOptions: 0);
        }
        catch (WindowsNativeCallException exception) when (exception.NtStatus is { } status && WindowsNative.IsPositiveAbsence(status))
        {
            return null;
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("inspect a child without following its final reparse point", exception);
        }
    }

    private static PhysicalStoreEntryInfo ToEntryInfo(WindowsNative.WindowsNativeEntry entry)
    {
        var fileId = new byte[16];
        BitConverter.TryWriteBytes(fileId.AsSpan(0, 8), entry.Identity.FileIdLow);
        BitConverter.TryWriteBytes(fileId.AsSpan(8, 8), entry.Identity.FileIdHigh);
        var volume = entry.Identity.VolumeSerialNumber;
        if (volume == 0 || fileId.All(static value => value == 0))
        {
            throw Refusal(
                PackageStoreAdmissionReason.UnsupportedFilesystem,
                "The filesystem did not provide a usable volume and full file identity.");
        }

        if (entry.Standard.EndOfFile < 0)
            throw Refusal(PackageStoreAdmissionReason.UnsupportedFilesystem, "The filesystem returned a negative file length.");
        if (entry.Standard.NumberOfLinks == 0)
            throw Refusal(PackageStoreAdmissionReason.UnsupportedFilesystem, "The filesystem did not provide a positive hard-link count.");

        var tag = (entry.Attributes.FileAttributes & WindowsNative.FileAttributeReparsePoint) != 0
            ? entry.Attributes.ReparseTag
            : 0;
        var kind = tag switch
        {
            WindowsNative.ReparseTagSymbolicLink or WindowsNative.ReparseTagMountPoint => PhysicalStoreEntryKind.SymbolicLink,
            _ when tag != 0 => PhysicalStoreEntryKind.Other,
            _ when (entry.Attributes.FileAttributes & WindowsNative.FileAttributeDevice) != 0 => PhysicalStoreEntryKind.Other,
            _ when entry.Standard.Directory != 0 => PhysicalStoreEntryKind.Directory,
            _ => PhysicalStoreEntryKind.RegularFile
        };

        return new PhysicalStoreEntryInfo(
            kind,
            new PhysicalFileIdentity(
                "windows-ntfs-x64",
                volume.ToString("X16", System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToHexString(fileId)),
            entry.Standard.NumberOfLinks,
            entry.Standard.EndOfFile,
            tag == 0 ? null : tag);
    }

    private static PhysicalStoreEntryInfo QueryEntry(IntPtr handle, string operation)
    {
        try
        {
            return ToEntryInfo(WindowsNative.QueryEntry(handle));
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure(operation, exception);
        }
    }

    internal static string ParseReparseTarget(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 8)
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority, "The reparse buffer was shorter than its fixed header.");

        var tag = BitConverter.ToUInt32(data, 0);
        var dataLength = BitConverter.ToUInt16(data, 4);
        if (dataLength > data.Length - 8)
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority, "The reparse buffer length exceeded the returned data.");

        int pathStart;
        ushort nameOffset;
        ushort nameLength;
        bool? isRelative = null;
        if (tag == WindowsNative.ReparseTagMountPoint)
        {
            if (dataLength < 8)
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority, "The mount-point reparse payload was truncated.");
            nameOffset = BitConverter.ToUInt16(data, 8);
            nameLength = BitConverter.ToUInt16(data, 10);
            pathStart = 16;
        }
        else if (tag == WindowsNative.ReparseTagSymbolicLink)
        {
            if (dataLength < 12)
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority, "The symbolic-link reparse payload was truncated.");
            nameOffset = BitConverter.ToUInt16(data, 8);
            nameLength = BitConverter.ToUInt16(data, 10);
            var flags = BitConverter.ToUInt32(data, 16);
            if ((flags & ~1u) != 0)
                throw Refusal(PackageStoreAdmissionReason.UnsupportedFilesystem, "The symbolic-link reparse flags are not supported.");
            isRelative = (flags & 1u) != 0;
            pathStart = 20;
        }
        else
        {
            throw Refusal(PackageStoreAdmissionReason.UnsupportedFilesystem, $"Reparse tag 0x{tag:X8} is not supported.");
        }

        var pathBufferLength = checked(8 + dataLength - pathStart);
        var relativeEnd = (uint)nameOffset + nameLength;
        if ((nameOffset & 1) != 0 || (nameLength & 1) != 0 || nameLength == 0 || relativeEnd > pathBufferLength)
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority, "The reparse substitute name has invalid bounds.");

        string target;
        try
        {
            target = StrictUnicode.GetString(data, pathStart + nameOffset, nameLength);
        }
        catch (DecoderFallbackException exception)
        {
            throw Unknown("The reparse substitute name is not valid UTF-16.", exception);
        }

        if (target.Length == 0 || target.IndexOf('\0') >= 0)
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority, "The reparse substitute name is empty or contains NUL.");

        if (isRelative == true)
        {
            if (target[0] is '\\' or '/' || target.Contains('/') ||
                target.Length >= 2 && char.IsAsciiLetter(target[0]) && target[1] == ':')
            {
                throw Unknown("The symbolic-link relative flag does not match its rooted or drive-relative target.");
            }
        }
        else if (!IsSupportedAbsoluteSubstitutePath(target))
        {
            throw Unknown("The absolute reparse target is not a local NT drive or volume path.");
        }

        return target;
    }

    private static bool IsSupportedAbsoluteSubstitutePath(string target)
    {
        const string NtPrefix = "\\??\\";
        if (!target.StartsWith(NtPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var path = target[NtPrefix.Length..];
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
            return !path.Contains('/');

        const string VolumePrefix = "Volume{";
        return path.Length >= VolumePrefix.Length + 38 &&
               path.StartsWith(VolumePrefix, StringComparison.OrdinalIgnoreCase) &&
               path[VolumePrefix.Length + 36] == '}' &&
               path[VolumePrefix.Length + 37] == '\\' &&
               Guid.TryParseExact(path.AsSpan(VolumePrefix.Length, 36), "D", out _) &&
               !path.Contains('/');
    }

    private static uint AccessFor(FileAccess access)
        => access switch
        {
            FileAccess.Read => WindowsNative.FileReadData,
            FileAccess.Write => WindowsNative.FileWriteData,
            FileAccess.ReadWrite => WindowsNative.FileReadData | WindowsNative.FileWriteData,
            _ => throw new ArgumentOutOfRangeException(nameof(access))
        };

    private static void ValidateNamespaceAnchor(string anchor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchor);
        var isDriveRoot = anchor.Length == 3 && char.IsAsciiLetter(anchor[0]) && anchor[1] == ':' && anchor[2] == '\\';
        var isVolumeRoot = anchor.Length == 49 &&
                           anchor.StartsWith("\\\\?\\Volume{", StringComparison.OrdinalIgnoreCase) &&
                           anchor[^2] == '}' && anchor[^1] == '\\' &&
                           Guid.TryParseExact(anchor.AsSpan(11, 36), "D", out _);
        if (!isDriveRoot && !isVolumeRoot)
        {
            throw Refusal(
                PackageStoreAdmissionReason.UnsupportedFilesystem,
                "The Windows provider accepts only a drive root (X:\\) or a qualified volume GUID root.");
        }
    }

    private void ValidateName(string name)
    {
        PhysicalStoreNames.ValidateSingleComponent(name);
        if (name.Length > 255)
            throw Refusal(PackageStoreAdmissionReason.UnsupportedFilesystem, "The Windows provider supports components up to 255 UTF-16 code units.");
    }

    private static void RequireControlFile(PhysicalStoreEntryInfo info, string message)
    {
        if (info.Kind != PhysicalStoreEntryKind.RegularFile || info.LinkCount != 1)
            throw Unknown(message);
    }

    private static void RequireKind(PhysicalStoreEntryInfo info, PhysicalStoreEntryKind expected, string message)
    {
        if (info.Kind != expected)
            throw Unknown(message);
    }

    private static void RequireSameVolume(PhysicalFileIdentity parent, PhysicalFileIdentity child)
    {
        if (!string.Equals(parent.VolumeOrDeviceId, child.VolumeOrDeviceId, StringComparison.Ordinal))
            throw Unknown("A child entry did not remain on the held NTFS volume.");
    }

    private static void RequireLocalDisk(WindowsNative.FileSystemDeviceInfo device)
    {
        const uint FileDeviceDisk = 7;
        const uint FileRemoteDevice = 0x00000010;
        if (device.DeviceType != FileDeviceDisk || (device.Characteristics & FileRemoteDevice) != 0)
        {
            throw Refusal(
                PackageStoreAdmissionReason.UnsupportedFilesystem,
                "The namespace root is not a positively identified local disk volume.");
        }
    }

    private WindowsHandleState GetState(PhysicalStoreHandle handle)
    {
        if (!_states.TryGetValue(handle, out var state))
        {
            throw new PackageStoreAdmissionException(
                PackageStoreAdmissionReason.RootMismatch,
                "The handle has no Windows filesystem state owned by this provider.");
        }

        return state;
    }

    private static IntPtr TakeRawHandle(SafeFileHandle source)
    {
        var handle = source.DangerousGetHandle();
        source.SetHandleAsInvalid();
        return handle;
    }

    private static void RequireSupportedPlatform()
    {
        if (!IsSupportedPlatform)
        {
            throw Refusal(
                PackageStoreAdmissionReason.UnsupportedFilesystem,
                "The Windows physical store provider is qualified only for Windows x64; other runtimes must use a qualified provider.");
        }
    }

    private static PackageStoreAdmissionException NativeFailure(string operation, WindowsNativeCallException exception)
    {
        var unsupported = exception.Unsupported ||
                          exception.NtStatus is { } status && WindowsNative.IsUnsupportedStatus(status) ||
                          exception.ErrorCode is WindowsNative.ErrorNotSupported or WindowsNative.ErrorInvalidFunction;
        return Refusal(
            unsupported ? PackageStoreAdmissionReason.UnsupportedFilesystem : PackageStoreAdmissionReason.UnknownAuthority,
            $"The Windows provider could not {operation}: {exception.Message}",
            exception);
    }

    private static PackageStoreAdmissionException NativeError(string operation, int error)
    {
        var unsupported = error is WindowsNative.ErrorNotSupported or WindowsNative.ErrorInvalidFunction;
        return Refusal(
            unsupported ? PackageStoreAdmissionReason.UnsupportedFilesystem : PackageStoreAdmissionReason.UnknownAuthority,
            $"The Windows provider could not {operation} (Win32Error={error}).");
    }

    private static PackageStoreAdmissionException Unknown(string message, Exception? inner = null)
        => Refusal(PackageStoreAdmissionReason.UnknownAuthority, message, inner);

    private static PackageStoreAdmissionException Refusal(
        PackageStoreAdmissionReason reason,
        string message,
        Exception? inner = null)
        => new(reason, message, innerException: inner);

    private sealed record WindowsHandleState(PhysicalFileIdentity Identity, WindowsAncestryFrame? Ancestry);

    private sealed class WindowsFileLockOwner(
        object providerToken,
        PhysicalStoreFileHandle file,
        PhysicalStoreSafeHandleLease handleLease,
        WindowsNative.Overlapped overlapped) : IAsyncDisposable
    {
        private readonly object _gate = new();
        private Task? _disposeTask;

        public ValueTask DisposeAsync()
        {
            TaskCompletionSource completion;
            lock (_gate)
            {
                if (_disposeTask is not null)
                    return new ValueTask(_disposeTask);
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = completion.Task;
            }

            Release(completion);
            return new ValueTask(completion.Task);
        }

        private void Release(TaskCompletionSource completion)
        {
            List<Exception>? failures = null;
            var unlocked = false;
            try
            {
                var nativeOverlapped = overlapped;
                WindowsNative.Unlock(handleLease.DangerousHandle, ref nativeOverlapped);
                unlocked = true;
            }
            catch (WindowsNativeCallException exception)
            {
                (failures ??= []).Add(NativeFailure("release the exclusive file lock", exception));
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            try
            {
                if (unlocked)
                    file.ReleaseLockReservation(providerToken);
                else
                    file.MarkLockReleaseFailed(providerToken);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            try
            {
                handleLease.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            if (failures is null)
                completion.TrySetResult();
            else if (failures.Count == 1)
                completion.TrySetException(failures[0]);
            else
                completion.TrySetException(new AggregateException("The Windows lock owner could not release all owned resources.", failures));
        }
    }
}
