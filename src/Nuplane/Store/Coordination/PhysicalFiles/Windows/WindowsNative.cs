using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Nuplane.Store.Coordination.PhysicalFiles.Windows;

/// <summary>Isolates the Windows handle-relative and bounded file APIs used by the store adapter.</summary>
internal static partial class WindowsNative
{
    internal const uint FileReadData = 0x0001;
    internal const uint FileWriteData = 0x0002;
    internal const uint FileAppendData = 0x0004;
    internal const uint FileListDirectory = 0x0001;
    internal const uint FileReadAttributes = 0x0080;
    internal const uint Synchronize = 0x00100000;

    internal const uint ShareRead = 0x00000001;
    internal const uint ShareWrite = 0x00000002;

    internal const uint FileAttributeDirectory = 0x00000010;
    internal const uint FileAttributeNormal = 0x00000080;
    internal const uint FileAttributeReparsePoint = 0x00000400;
    internal const uint FileAttributeDevice = 0x00000040;

    internal const uint FileOpenReparsePoint = 0x00200000;
    internal const uint FileSynchronousIoNonAlert = 0x00000020;
    internal const uint FileDirectoryFile = 0x00000001;
    internal const uint FileNonDirectoryFile = 0x00000040;
    internal const uint FileOpen = 0x00000001;
    internal const uint FileCreate = 0x00000002;

    internal const uint ReparseTagMountPoint = 0xA0000003;
    internal const uint ReparseTagSymbolicLink = 0xA000000C;
    internal const uint FsctlGetReparsePoint = 0x000900A8;
    internal const uint MaximumReparseDataBufferSize = 16 * 1024;

    internal const int ErrorLockViolation = 33;
    internal const int ErrorFileNotFound = 2;
    internal const int ErrorPathNotFound = 3;
    internal const int ErrorNotSupported = 50;
    internal const int ErrorInvalidFunction = 1;

    internal const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
    internal const int StatusObjectPathNotFound = unchecked((int)0xC000003A);
    internal const int StatusObjectNameCollision = unchecked((int)0xC0000035);
    internal const int StatusNotSupported = unchecked((int)0xC00000BB);
    internal const int StatusInvalidDeviceRequest = unchecked((int)0xC0000010);
    internal const int StatusEndOfFile = unchecked((int)0xC0000011);
    internal const int StatusPending = 0x00000103;

    internal static SafeFileHandle OpenNamespaceRoot(string anchor)
    {
        var handle = CreateFileW(
            anchor,
            FileReadAttributes | FileListDirectory | Synchronize,
            ShareRead | ShareWrite,
            IntPtr.Zero,
            3,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new WindowsNativeCallException("CreateFileW could not open the namespace root.", errorCode: error);
        }

        return handle;
    }

    internal static SafeFileHandle OpenRelative(
        IntPtr parent,
        string component,
        uint desiredAccess,
        uint disposition,
        uint createOptions,
        uint attributes = FileAttributeNormal,
        uint shareAccess = ShareRead | ShareWrite)
    {
        var byteLength = checked(component.Length * sizeof(char));
        if (byteLength > ushort.MaxValue - sizeof(char))
            throw new ArgumentException("A Windows path component is too long for a native relative open.", nameof(component));

        var nameBuffer = Marshal.StringToHGlobalUni(component);
        var unicodeStringPointer = IntPtr.Zero;
        try
        {
            unicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(
                new UnicodeString
                {
                    Length = checked((ushort)byteLength),
                    MaximumLength = checked((ushort)(byteLength + sizeof(char))),
                    Buffer = nameBuffer
                },
                unicodeStringPointer,
                fDeleteOld: false);

            var caseSensitiveBefore = QueryDirectoryCaseSensitive(parent);
            var objectAttributes = new ObjectAttributes
            {
                Length = checked((uint)Marshal.SizeOf<ObjectAttributes>()),
                RootDirectory = parent,
                ObjectName = unicodeStringPointer,
                // Follow the held directory's native lookup profile; do not emulate it with managed folding.
                Attributes = caseSensitiveBefore ? 0u : ObjectCaseInsensitive
            };
            var status = NtCreateFile(
                out var rawHandle,
                desiredAccess,
                ref objectAttributes,
                out var ioStatus,
                IntPtr.Zero,
                attributes,
                shareAccess,
                disposition,
                createOptions | FileSynchronousIoNonAlert | FileOpenReparsePoint,
                IntPtr.Zero,
                0);

            var ioStatusCode = unchecked((int)ioStatus.Status.ToInt64());
            if (status == StatusPending || ioStatusCode == StatusPending)
            {
                if (rawHandle != IntPtr.Zero && rawHandle != new IntPtr(-1))
                    _ = CloseHandle(rawHandle);
                throw new WindowsNativeCallException("Synchronous NtCreateFile unexpectedly returned STATUS_PENDING.", unsupported: true);
            }

            bool caseSensitiveAfter;
            try
            {
                caseSensitiveAfter = QueryDirectoryCaseSensitive(parent);
            }
            catch
            {
                if (rawHandle != IntPtr.Zero && rawHandle != new IntPtr(-1))
                    _ = CloseHandle(rawHandle);
                throw;
            }

            if (caseSensitiveAfter != caseSensitiveBefore)
            {
                if (rawHandle != IntPtr.Zero && rawHandle != new IntPtr(-1))
                    _ = CloseHandle(rawHandle);
                throw new WindowsNativeCallException("The held directory's case-sensitivity profile changed during a relative lookup.");
            }

            if (status < 0 || ioStatusCode < 0)
            {
                if (rawHandle != IntPtr.Zero && rawHandle != new IntPtr(-1))
                    _ = CloseHandle(rawHandle);
                throw new WindowsNativeCallException(
                    "NtCreateFile could not open one child component.",
                    ntStatus: status < 0 ? status : ioStatusCode);
            }

            if (rawHandle == IntPtr.Zero || rawHandle == new IntPtr(-1))
                throw new WindowsNativeCallException("NtCreateFile returned an invalid handle.", ntStatus: status);

            return new SafeFileHandle(rawHandle, ownsHandle: true);
        }
        finally
        {
            if (unicodeStringPointer != IntPtr.Zero)
                Marshal.FreeHGlobal(unicodeStringPointer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    internal static SafeFileHandle Duplicate(SafeFileHandle source)
    {
        var addedReference = false;
        source.DangerousAddRef(ref addedReference);
        try
        {
            if (!DuplicateHandle(
                    GetCurrentProcess(),
                    source.DangerousGetHandle(),
                    GetCurrentProcess(),
                    out var duplicate,
                    0,
                    inheritHandle: false,
                    options: 2)) // DUPLICATE_SAME_ACCESS.
            {
                throw new WindowsNativeCallException("DuplicateHandle could not retain a directory handle.", errorCode: Marshal.GetLastPInvokeError());
            }

            return new SafeFileHandle(duplicate, ownsHandle: true);
        }
        finally
        {
            if (addedReference)
                source.DangerousRelease();
        }
    }

    internal static SafeFileHandle Duplicate(IntPtr source)
    {
        if (!DuplicateHandle(
                GetCurrentProcess(),
                source,
                GetCurrentProcess(),
                out var duplicate,
                0,
                inheritHandle: false,
                options: 2))
        {
            throw new WindowsNativeCallException("DuplicateHandle could not retain a directory handle.", errorCode: Marshal.GetLastPInvokeError());
        }

        return new SafeFileHandle(duplicate, ownsHandle: true);
    }

    internal static WindowsNativeEntry QueryEntry(IntPtr handle)
    {
        if (!GetFileInformationByHandleEx(handle, 18, out FileIdInfo identity, checked((uint)Marshal.SizeOf<FileIdInfo>()))) // FileIdInfo.
            throw new WindowsNativeCallException("Windows could not provide the full file identity.", errorCode: Marshal.GetLastPInvokeError());
        if (!GetFileInformationByHandleEx(handle, 1, out FileStandardInfo standard, checked((uint)Marshal.SizeOf<FileStandardInfo>()))) // FileStandardInfo.
            throw new WindowsNativeCallException("Windows could not provide file type and link metadata.", errorCode: Marshal.GetLastPInvokeError());
        if (!GetFileInformationByHandleEx(handle, 9, out FileAttributeTagInfo attributes, checked((uint)Marshal.SizeOf<FileAttributeTagInfo>()))) // FileAttributeTagInfo.
            throw new WindowsNativeCallException("Windows could not provide file attribute and reparse metadata.", errorCode: Marshal.GetLastPInvokeError());

        return new WindowsNativeEntry(identity, standard, attributes);
    }

    internal static string GetFileSystemName(IntPtr handle)
    {
        var fileSystem = new StringBuilder(64);
        if (!GetVolumeInformationByHandleW(
                handle,
                IntPtr.Zero,
                0,
                out _,
                out _,
                out _,
                fileSystem,
                checked((uint)fileSystem.Capacity)))
        {
            throw new WindowsNativeCallException("Windows could not identify the root filesystem.", errorCode: Marshal.GetLastPInvokeError());
        }

        return fileSystem.ToString();
    }

    internal static FileSystemDeviceInfo QueryDeviceInfo(IntPtr handle)
    {
        var status = NtQueryVolumeInformationFile(
            handle,
            out var ioStatus,
            out FileSystemDeviceInfo deviceInfo,
            checked((uint)Marshal.SizeOf<FileSystemDeviceInfo>()),
            4); // FileFsDeviceInformation.
        var completedStatus = unchecked((int)ioStatus.Status.ToInt64());
        if (status == StatusPending || completedStatus == StatusPending)
            throw new WindowsNativeCallException("Synchronous volume-device metadata unexpectedly remained pending.", unsupported: true);
        if (status < 0)
            throw new WindowsNativeCallException("NtQueryVolumeInformationFile could not identify the volume device.", ntStatus: status);
        if (completedStatus < 0)
            throw new WindowsNativeCallException("NtQueryVolumeInformationFile returned a failed IO status.", ntStatus: completedStatus);
        return deviceInfo;
    }

    internal static string GetVolumeGuidRoot(string driveRoot)
    {
        var volumeName = new StringBuilder(128);
        if (!GetVolumeNameForVolumeMountPointW(driveRoot, volumeName, checked((uint)volumeName.Capacity)))
            throw new WindowsNativeCallException("GetVolumeNameForVolumeMountPointW could not identify the drive root volume.", errorCode: Marshal.GetLastPInvokeError());
        return volumeName.ToString();
    }

    internal static byte[] ReadAt(IntPtr handle, int offset, int count)
    {
        var buffer = new byte[count];
        if (count == 0)
            return buffer;

        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var byteOffset = (long)offset;
            var status = NtReadFile(
                handle,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                out var ioStatus,
                IntPtr.Add(pin.AddrOfPinnedObject(), 0),
                checked((uint)count),
                ref byteOffset,
                IntPtr.Zero);
            if (status == StatusPending || unchecked((int)ioStatus.Status.ToInt64()) == StatusPending)
                throw new WindowsNativeCallException("A synchronous NtReadFile unexpectedly returned STATUS_PENDING.", unsupported: true);
            var ioStatusCode = unchecked((int)ioStatus.Status.ToInt64());
            if (ioStatusCode < 0)
                throw new WindowsNativeCallException("NtReadFile returned a failed IO status.", ntStatus: ioStatusCode);
            var completedStatus = status;
            if (completedStatus == StatusEndOfFile)
                return Array.Empty<byte>();
            if (completedStatus < 0)
                throw new WindowsNativeCallException("NtReadFile could not read a bounded control file.", ntStatus: completedStatus);

            var bytesRead = ioStatus.Information.ToInt64();
            if (bytesRead < 0 || bytesRead > count)
                throw new WindowsNativeCallException("NtReadFile returned an invalid byte count.", unsupported: true);
            if (bytesRead != count)
                Array.Resize(ref buffer, checked((int)bytesRead));
            return buffer;
        }
        finally
        {
            pin.Free();
        }
    }

    internal static int ReadPackageFileAt(IntPtr handle, long offset, byte[] buffer, int bufferOffset, int count)
    {
        if (offset < 0 || bufferOffset < 0 || count < 0 || bufferOffset > buffer.Length - count)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (count == 0)
            return 0;

        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var byteOffset = offset;
            var status = NtReadFile(
                handle,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                out var ioStatus,
                IntPtr.Add(pin.AddrOfPinnedObject(), bufferOffset),
                checked((uint)count),
                ref byteOffset,
                IntPtr.Zero);
            if (status == StatusPending || unchecked((int)ioStatus.Status.ToInt64()) == StatusPending)
                throw new WindowsNativeCallException("A synchronous package archive read unexpectedly returned STATUS_PENDING.", unsupported: true);
            var ioStatusCode = unchecked((int)ioStatus.Status.ToInt64());
            if (status == StatusEndOfFile || ioStatusCode == StatusEndOfFile)
                return 0;
            if (ioStatusCode < 0)
                throw new WindowsNativeCallException("NtReadFile returned a failed package archive read.", ntStatus: ioStatusCode);
            if (status < 0)
                throw new WindowsNativeCallException("NtReadFile could not read the bounded package archive.", ntStatus: status);

            var bytesRead = ioStatus.Information.ToInt64();
            if (bytesRead < 0 || bytesRead > count)
                throw new WindowsNativeCallException("NtReadFile returned an invalid package archive byte count.", unsupported: true);
            return checked((int)bytesRead);
        }
        finally
        {
            pin.Free();
        }
    }

    internal static int WriteAt(IntPtr handle, ReadOnlyMemory<byte> contents, int offset)
    {
        if (contents.IsEmpty)
            return 0;
        var data = contents.ToArray();
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var byteOffset = (long)offset;
            var status = NtWriteFile(
                handle,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                out var ioStatus,
                pin.AddrOfPinnedObject(),
                checked((uint)data.Length),
                ref byteOffset,
                IntPtr.Zero);
            if (status == StatusPending || unchecked((int)ioStatus.Status.ToInt64()) == StatusPending)
                throw new WindowsNativeCallException("A synchronous NtWriteFile unexpectedly returned STATUS_PENDING.", unsupported: true);
            var ioStatusCode = unchecked((int)ioStatus.Status.ToInt64());
            if (ioStatusCode < 0)
                throw new WindowsNativeCallException("NtWriteFile returned a failed IO status.", ntStatus: ioStatusCode);
            var completedStatus = status;
            if (completedStatus < 0)
                throw new WindowsNativeCallException("NtWriteFile could not write a control file.", ntStatus: completedStatus);

            var bytesWritten = ioStatus.Information.ToInt64();
            if (bytesWritten < 0 || bytesWritten > data.Length)
                throw new WindowsNativeCallException("NtWriteFile returned an invalid byte count.", unsupported: true);
            return checked((int)bytesWritten);
        }
        finally
        {
            pin.Free();
        }
    }

    internal static int WritePackageFileAt(IntPtr handle, long offset, byte[] buffer, int bufferOffset, int count)
    {
        if (offset < 0 || bufferOffset < 0 || count < 0 || bufferOffset > buffer.Length - count)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (count == 0)
            return 0;

        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var byteOffset = offset;
            var status = NtWriteFile(
                handle,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                out var ioStatus,
                IntPtr.Add(pin.AddrOfPinnedObject(), bufferOffset),
                checked((uint)count),
                ref byteOffset,
                IntPtr.Zero);
            if (status == StatusPending || unchecked((int)ioStatus.Status.ToInt64()) == StatusPending)
                throw new WindowsNativeCallException("A synchronous package file write unexpectedly returned STATUS_PENDING.", unsupported: true);
            var ioStatusCode = unchecked((int)ioStatus.Status.ToInt64());
            if (ioStatusCode < 0)
                throw new WindowsNativeCallException("NtWriteFile returned a failed package file write.", ntStatus: ioStatusCode);
            if (status < 0)
                throw new WindowsNativeCallException("NtWriteFile could not write the bounded package file.", ntStatus: status);

            var bytesWritten = ioStatus.Information.ToInt64();
            if (bytesWritten < 0 || bytesWritten > count)
                throw new WindowsNativeCallException("NtWriteFile returned an invalid package file byte count.", unsupported: true);
            return checked((int)bytesWritten);
        }
        finally
        {
            pin.Free();
        }
    }

    internal static void Flush(IntPtr handle)
    {
        if (!FlushFileBuffers(handle))
            throw new WindowsNativeCallException("FlushFileBuffers could not flush the control file.", errorCode: Marshal.GetLastPInvokeError());
    }

    internal static bool TryLock(IntPtr handle, ref Overlapped overlapped)
        => LockFileEx(handle, 0x00000003, 0, 1, 0, ref overlapped); // LOCKFILE_FAIL_IMMEDIATELY | LOCKFILE_EXCLUSIVE_LOCK.

    internal static void Unlock(IntPtr handle, ref Overlapped overlapped)
    {
        if (!UnlockFileEx(handle, 0, 1, 0, ref overlapped))
            throw new WindowsNativeCallException("UnlockFileEx could not release the file lock.", errorCode: Marshal.GetLastPInvokeError());
    }

    internal static byte[] ReadReparseData(IntPtr handle)
    {
        var output = new byte[MaximumReparseDataBufferSize];
        if (!DeviceIoControl(
                handle,
                FsctlGetReparsePoint,
                IntPtr.Zero,
                0,
                output,
                checked((uint)output.Length),
                out var returned,
                IntPtr.Zero))
        {
            throw new WindowsNativeCallException("FSCTL_GET_REPARSE_POINT could not read the held reparse point.", errorCode: Marshal.GetLastPInvokeError());
        }

        if (returned < 8 || returned > (uint)output.Length)
            throw new WindowsNativeCallException("FSCTL_GET_REPARSE_POINT returned an invalid bounded length.", unsupported: true);
        Array.Resize(ref output, checked((int)returned));
        return output;
    }

    internal static bool IsPositiveAbsence(int status)
        => status is StatusObjectNameNotFound or StatusObjectPathNotFound;

    internal static bool IsUnsupportedStatus(int status)
        => status is StatusNotSupported or StatusInvalidDeviceRequest;

    internal static bool CloseHandle(IntPtr handle) => CloseHandleNative(handle);

    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        internal ushort Length;
        internal ushort MaximumLength;
        internal IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        internal uint Length;
        internal IntPtr RootDirectory;
        internal IntPtr ObjectName;
        internal uint Attributes;
        internal IntPtr SecurityDescriptor;
        internal IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        internal IntPtr Status;
        internal IntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Overlapped
    {
        internal IntPtr Internal;
        internal IntPtr InternalHigh;
        internal uint Offset;
        internal uint OffsetHigh;
        internal IntPtr Event;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileIdInfo
    {
        internal ulong VolumeSerialNumber;
        internal ulong FileIdLow;
        internal ulong FileIdHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileStandardInfo
    {
        internal long AllocationSize;
        internal long EndOfFile;
        internal uint NumberOfLinks;
        internal byte DeletePending;
        internal byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileAttributeTagInfo
    {
        internal uint FileAttributes;
        internal uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileSystemDeviceInfo
    {
        internal uint DeviceType;
        internal uint Characteristics;
    }

    internal readonly record struct WindowsNativeEntry(
        FileIdInfo Identity,
        FileStandardInfo Standard,
        FileAttributeTagInfo Attributes);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(IntPtr file, int fileInformationClass, out FileIdInfo information, uint bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(IntPtr file, int fileInformationClass, out FileStandardInfo information, uint bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(IntPtr file, int fileInformationClass, out FileAttributeTagInfo information, uint bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(
        IntPtr file,
        IntPtr volumeNameBuffer,
        uint volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer,
        uint fileSystemNameSize);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string volumeMountPoint, StringBuilder volumeName, uint bufferLength);

    [DllImport("ntdll.dll", EntryPoint = "NtQueryVolumeInformationFile")]
    private static extern int NtQueryVolumeInformationFile(
        IntPtr fileHandle,
        out IoStatusBlock ioStatusBlock,
        out FileSystemDeviceInfo fileSystemInformation,
        uint length,
        int fileSystemInformationClass);

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", EntryPoint = "DuplicateHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcess,
        IntPtr sourceHandle,
        IntPtr targetProcess,
        out IntPtr targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandleNative(IntPtr handle);

    [DllImport("ntdll.dll", EntryPoint = "NtCreateFile")]
    private static extern int NtCreateFile(
        out IntPtr fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll", EntryPoint = "NtReadFile")]
    private static extern int NtReadFile(
        IntPtr fileHandle,
        IntPtr eventHandle,
        IntPtr apcRoutine,
        IntPtr apcContext,
        out IoStatusBlock ioStatusBlock,
        IntPtr buffer,
        uint length,
        ref long byteOffset,
        IntPtr key);

    [DllImport("ntdll.dll", EntryPoint = "NtWriteFile")]
    private static extern int NtWriteFile(
        IntPtr fileHandle,
        IntPtr eventHandle,
        IntPtr apcRoutine,
        IntPtr apcContext,
        out IoStatusBlock ioStatusBlock,
        IntPtr buffer,
        uint length,
        ref long byteOffset,
        IntPtr key);

    [DllImport("kernel32.dll", EntryPoint = "FlushFileBuffers", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(IntPtr file);

    [DllImport("kernel32.dll", EntryPoint = "LockFileEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockFileEx(IntPtr file, uint flags, uint reserved, uint bytesLow, uint bytesHigh, ref Overlapped overlapped);

    [DllImport("kernel32.dll", EntryPoint = "UnlockFileEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnlockFileEx(IntPtr file, uint reserved, uint bytesLow, uint bytesHigh, ref Overlapped overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        IntPtr device,
        uint controlCode,
        IntPtr inputBuffer,
        uint inputBufferSize,
        [Out] byte[] outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}

internal sealed class WindowsNativeCallException : IOException
{
    internal WindowsNativeCallException(string message, int? ntStatus = null, int? errorCode = null, bool unsupported = false)
        : base(Format(message, ntStatus, errorCode))
    {
        NtStatus = ntStatus;
        ErrorCode = errorCode;
        Unsupported = unsupported;
    }

    internal int? NtStatus { get; }
    internal int? ErrorCode { get; }
    internal bool Unsupported { get; }

    private static string Format(string message, int? ntStatus, int? errorCode)
    {
        if (ntStatus is not null)
            return $"{message} NTSTATUS=0x{ntStatus.Value:X8}.";
        if (errorCode is not null)
            return $"{message} Win32Error={errorCode.Value}.";
        return message;
    }
}
