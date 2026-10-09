using System.Runtime.InteropServices;

namespace Nuplane.Store.Coordination.PhysicalFiles.Unix;

internal enum UnixPlatform
{
    Darwin,
    Linux
}

internal enum UnixStatResultStatus
{
    Success,
    Absent,
    Failure
}

internal readonly record struct UnixStatResult(UnixStatResultStatus Status, UnixMetadata Metadata, int Error);

internal readonly record struct UnixMetadata(
    UnixPlatform Platform,
    int Mode,
    ulong LinkCount,
    ulong Inode,
    string Device,
    long Length);

internal enum UnixLockStatus
{
    Acquired,
    Busy,
    Failure
}

internal readonly record struct UnixLockResult(UnixLockStatus Status, int Error);

/// <summary>Private Darwin/Linux ABI boundary. Struct layouts are explicit and platform-gated.</summary>
internal static class UnixNative
{
    internal const int FileTypeMask = 0xF000;
    internal const int FileTypeRegular = 0x8000;
    internal const int FileTypeDirectory = 0x4000;
    internal const int FileTypeSymbolicLink = 0xA000;

    private const int DarwinOpenNoFollow = 0x100;
    private const int DarwinOpenCreate = 0x200;
    private const int DarwinOpenExclusive = 0x800;
    private const int DarwinOpenNonBlocking = 0x4;
    private const int DarwinOpenDirectory = 0x100000;
    private const int DarwinOpenCloseOnExec = 0x1000000;
    private const int DarwinAtSymlinkNoFollow = 0x20;

    private const int LinuxOpenNonBlocking = 0x800;
    private const int LinuxOpenCloseOnExec = 0x80000;
    private const int LinuxOpenNoFollow = 0x20000;
    private const int LinuxOpenDirectory = 0x10000;
    private const int LinuxOpenCreate = 0x40;
    private const int LinuxOpenExclusive = 0x80;
    private const int LinuxAtSymlinkNoFollow = 0x100;
    private const int LinuxAtNoAutomount = 0x800;
    private const int LinuxAtEmptyPath = 0x1000;
    private const uint LinuxStatxBasicStats = 0x7ff;
    private const uint LinuxStatxRequired = 0x1 | 0x2 | 0x4 | 0x100 | 0x200;

    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int LockUnlock = 8;
    private const int DarwinWouldBlock = 35;
    private const int LinuxWouldBlock = 11;

    internal static UnixPlatform? GetPlatform()
    {
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return UnixPlatform.Darwin;

        if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64)
            return UnixPlatform.Linux;

        return null;
    }

    internal static int OpenNamespaceRoot(UnixPlatform platform)
    {
        var flags = platform switch
        {
            UnixPlatform.Darwin => DarwinOpenDirectory | DarwinOpenCloseOnExec | DarwinOpenNoFollow,
            UnixPlatform.Linux => LinuxOpenDirectory | LinuxOpenCloseOnExec | LinuxOpenNoFollow,
            _ => throw new ArgumentOutOfRangeException(nameof(platform))
        };

        var fd = platform == UnixPlatform.Darwin
            ? Darwin.Open("/", flags)
            : Linux.Open("/", flags, 0);
        return fd < 0 ? throw NativeResultException("open namespace root") : fd;
    }

    internal static UnixStatResult StatAt(UnixPlatform platform, int parentFd, string name)
    {
        if (platform == UnixPlatform.Darwin)
        {
            if (Darwin.FStatAt(parentFd, name, out var stat, DarwinAtSymlinkNoFollow) != 0)
                return StatError(Marshal.GetLastPInvokeError());
            return StatSuccess(ToMetadata(stat));
        }

        var flags = LinuxAtSymlinkNoFollow | LinuxAtNoAutomount;
        if (Linux.Statx(parentFd, name, flags, LinuxStatxBasicStats, out var statx) != 0)
            return StatError(Marshal.GetLastPInvokeError());
        return StatSuccess(ToMetadata(statx));
    }

    internal static UnixMetadata StatHandle(UnixPlatform platform, int fd)
    {
        if (platform == UnixPlatform.Darwin)
        {
            if (Darwin.FStat(fd, out var stat) != 0)
                throw NativeResultException("inspect held handle");
            return ToMetadata(stat);
        }

        if (Linux.Statx(fd, string.Empty, LinuxAtEmptyPath, LinuxStatxBasicStats, out var statx) != 0)
            throw NativeResultException("inspect held handle");
        return ToMetadata(statx);
    }

    internal static int OpenDirectoryAt(UnixPlatform platform, int parentFd, string name)
    {
        var flags = platform switch
        {
            UnixPlatform.Darwin => DarwinOpenDirectory | DarwinOpenCloseOnExec | DarwinOpenNoFollow,
            UnixPlatform.Linux => LinuxOpenDirectory | LinuxOpenCloseOnExec | LinuxOpenNoFollow,
            _ => throw new ArgumentOutOfRangeException(nameof(platform))
        };
        var fd = platform == UnixPlatform.Darwin
            ? Darwin.OpenAt(parentFd, name, flags)
            : Linux.OpenAt(parentFd, name, flags, 0);
        return fd < 0 ? throw NativeResultException("open child directory") : fd;
    }

    internal static int OpenFileAt(UnixPlatform platform, int parentFd, string name, FileAccess access)
    {
        var accessFlags = access switch
        {
            FileAccess.Read => 0,
            FileAccess.Write => 1,
            FileAccess.ReadWrite => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(access))
        };
        var flags = platform switch
        {
            UnixPlatform.Darwin => accessFlags | DarwinOpenCloseOnExec | DarwinOpenNoFollow | DarwinOpenNonBlocking,
            UnixPlatform.Linux => accessFlags | LinuxOpenCloseOnExec | LinuxOpenNoFollow | LinuxOpenNonBlocking,
            _ => throw new ArgumentOutOfRangeException(nameof(platform))
        };
        var fd = platform == UnixPlatform.Darwin
            ? Darwin.OpenAt(parentFd, name, flags)
            : Linux.OpenAt(parentFd, name, flags, 0);
        return fd < 0 ? throw NativeResultException("open child control file") : fd;
    }

    internal static int CreateDirectoryAt(UnixPlatform platform, int parentFd, string name)
    {
        var result = platform == UnixPlatform.Darwin
            ? Darwin.MkdirAt(parentFd, name, 0x1C0) // 0700
            : Linux.MkdirAt(parentFd, name, 0x1C0);
        return result != 0 ? throw NativeResultException("create child directory") : 0;
    }

    internal static int CreateFileAt(UnixPlatform platform, int parentFd, string name)
    {
        var flags = platform switch
        {
            UnixPlatform.Darwin => 2 | DarwinOpenCreate | DarwinOpenExclusive | DarwinOpenNoFollow | DarwinOpenCloseOnExec | DarwinOpenNonBlocking,
            UnixPlatform.Linux => 2 | LinuxOpenCreate | LinuxOpenExclusive | LinuxOpenNoFollow | LinuxOpenCloseOnExec | LinuxOpenNonBlocking,
            _ => throw new ArgumentOutOfRangeException(nameof(platform))
        };
        var fd = platform == UnixPlatform.Darwin
            ? Darwin.OpenAtCreate(parentFd, name, flags, 0x180) // 0600
            : Linux.OpenAt(parentFd, name, flags, 0x180);
        return fd < 0 ? throw NativeResultException("create control file exclusively") : fd;
    }

    internal static byte[] ReadLinkAt(UnixPlatform platform, int parentFd, string name, int maximumBytes)
    {
        var buffer = new byte[maximumBytes];
        var count = platform == UnixPlatform.Darwin
            ? Darwin.ReadLinkAt(parentFd, name, buffer, (nuint)buffer.Length)
            : Linux.ReadLinkAt(parentFd, name, buffer, (nuint)buffer.Length);
        if (count < 0)
            throw NativeResultException("read symbolic-link target");
        if ((nuint)count >= (nuint)buffer.Length)
            throw new IOException("The symbolic-link target exceeded the bounded read size.");
        return buffer.AsSpan(0, checked((int)count)).ToArray();
    }

    internal static int ReadAt(UnixPlatform platform, int fd, byte[] buffer, int offset)
    {
        var remaining = buffer.AsSpan(offset).ToArray();
        while (true)
        {
            var count = platform == UnixPlatform.Darwin
                ? Darwin.PRead(fd, remaining, (nuint)remaining.Length, offset)
                : Linux.PRead(fd, remaining, (nuint)remaining.Length, offset);
            if (count < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (IsInterrupted(error))
                    continue;
                throw new UnixNativeCallException(error, "read bounded control file");
            }

            var read = checked((int)count);
            if (read > 0)
                remaining.AsSpan(0, read).CopyTo(buffer.AsSpan(offset));
            return read;
        }
    }

    internal static int Write(UnixPlatform platform, int fd, byte[] buffer, int offset)
    {
        var remaining = buffer.AsSpan(offset).ToArray();
        while (true)
        {
            var count = platform == UnixPlatform.Darwin
                ? Darwin.Write(fd, remaining, (nuint)remaining.Length)
                : Linux.Write(fd, remaining, (nuint)remaining.Length);
            if (count < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (IsInterrupted(error))
                    continue;
                throw new UnixNativeCallException(error, "write new control file");
            }
            return checked((int)count);
        }
    }

    internal static int Flush(UnixPlatform platform, int fd)
    {
        var result = platform == UnixPlatform.Darwin ? Darwin.FSync(fd) : Linux.FSync(fd);
        return result != 0 ? throw NativeResultException("flush new control file") : 0;
    }

    internal static UnixLockResult TryLock(UnixPlatform platform, int fd)
    {
        var result = platform == UnixPlatform.Darwin
            ? Darwin.FLock(fd, LockExclusive | LockNonBlocking)
            : Linux.FLock(fd, LockExclusive | LockNonBlocking);
        if (result == 0)
            return new UnixLockResult(UnixLockStatus.Acquired, 0);

        var error = Marshal.GetLastPInvokeError();
        return error == WouldBlock(platform)
            ? new UnixLockResult(UnixLockStatus.Busy, error)
            : new UnixLockResult(UnixLockStatus.Failure, error);
    }

    internal static int Unlock(UnixPlatform platform, int fd)
    {
        var result = platform == UnixPlatform.Darwin ? Darwin.FLock(fd, LockUnlock) : Linux.FLock(fd, LockUnlock);
        return result != 0 ? throw NativeResultException("release exclusive file lock") : 0;
    }

    internal static bool IsUnsupportedError(int error)
    {
        var platform = GetPlatform();
        return platform switch
        {
            UnixPlatform.Darwin => error is 45 or 78 or 77, // ENOTSUP/EOPNOTSUPP, ENOSYS, ENOLCK
            UnixPlatform.Linux => error is 95 or 38 or 37, // EOPNOTSUPP, ENOSYS, ENOLCK
            _ => true
        };
    }

    private static UnixStatResult StatSuccess(UnixMetadata metadata)
        => new(UnixStatResultStatus.Success, metadata, 0);

    private static UnixStatResult StatError(int error)
        => error == 2
            ? new UnixStatResult(UnixStatResultStatus.Absent, default, error)
            : new UnixStatResult(UnixStatResultStatus.Failure, default, error);

    private static UnixMetadata ToMetadata(Darwin.DarwinStat stat)
        => new(
            UnixPlatform.Darwin,
            stat.Mode,
            stat.LinkCount,
            stat.Inode,
            stat.Device.ToString(System.Globalization.CultureInfo.InvariantCulture),
            stat.Length);

    private static UnixMetadata ToMetadata(Linux.LinuxStatx statx)
    {
        if ((statx.Mask & LinuxStatxRequired) != LinuxStatxRequired)
            throw new UnixNativeCallException(0, "statx omitted required type, mode, link count, inode, or size fields.", unsupported: true);
        if (statx.Size > long.MaxValue)
            throw new UnixNativeCallException(0, "statx returned a file size outside the supported range.", unsupported: true);

        return new UnixMetadata(
            UnixPlatform.Linux,
            statx.Mode,
            statx.LinkCount,
            statx.Inode,
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{statx.DeviceMajor}:{statx.DeviceMinor}"),
            (long)statx.Size);
    }

    private static int WouldBlock(UnixPlatform platform) => platform == UnixPlatform.Darwin ? DarwinWouldBlock : LinuxWouldBlock;

    private static bool IsInterrupted(int error) => error == 4; // EINTR on both supported Unix families.

    private static UnixNativeCallException NativeResultException(string operation)
        => new(Marshal.GetLastPInvokeError(), operation);

    private static class Darwin
    {
        [DllImport("libSystem.B.dylib", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int Open(string path, int flags);

        [DllImport("libSystem.B.dylib", EntryPoint = "openat", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int OpenAt(int parentFd, string name, int flags);

        [DllImport("nuplane_store_native", EntryPoint = "nuplane_openat_create", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int OpenAtCreate(int parentFd, string name, int flags, uint mode);

        [DllImport("libSystem.B.dylib", EntryPoint = "fstat", SetLastError = true)]
        internal static extern int FStat(int fd, out DarwinStat stat);

        [DllImport("libSystem.B.dylib", EntryPoint = "fstatat", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int FStatAt(int parentFd, string name, out DarwinStat stat, int flags);

        [DllImport("libSystem.B.dylib", EntryPoint = "mkdirat", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int MkdirAt(int parentFd, string name, uint mode);

        [DllImport("libSystem.B.dylib", EntryPoint = "readlinkat", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern nint ReadLinkAt(int parentFd, string name, [Out] byte[] buffer, nuint count);

        [DllImport("libSystem.B.dylib", EntryPoint = "pread", SetLastError = true)]
        internal static extern nint PRead(int fd, [Out] byte[] buffer, nuint count, long offset);

        [DllImport("libSystem.B.dylib", EntryPoint = "write", SetLastError = true)]
        internal static extern nint Write(int fd, [In] byte[] buffer, nuint count);

        [DllImport("libSystem.B.dylib", EntryPoint = "fsync", SetLastError = true)]
        internal static extern int FSync(int fd);

        [DllImport("libSystem.B.dylib", EntryPoint = "flock", SetLastError = true)]
        internal static extern int FLock(int fd, int operation);

        [StructLayout(LayoutKind.Explicit, Size = 144)]
        internal struct DarwinStat
        {
            [FieldOffset(0)] internal int Device;
            [FieldOffset(4)] internal ushort Mode;
            [FieldOffset(6)] internal ushort LinkCount;
            [FieldOffset(8)] internal ulong Inode;
            [FieldOffset(96)] internal long Length;
        }
    }

    private static class Linux
    {
        [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int Open(string path, int flags, uint mode);

        [DllImport("libc", EntryPoint = "openat", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int OpenAt(int parentFd, string name, int flags, uint mode);

        [DllImport("libc", EntryPoint = "statx", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int Statx(int directoryFd, string path, int flags, uint mask, out LinuxStatx statx);

        [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int MkdirAt(int parentFd, string name, uint mode);

        [DllImport("libc", EntryPoint = "readlinkat", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern nint ReadLinkAt(int parentFd, string name, [Out] byte[] buffer, nuint count);

        [DllImport("libc", EntryPoint = "pread", SetLastError = true)]
        internal static extern nint PRead(int fd, [Out] byte[] buffer, nuint count, long offset);

        [DllImport("libc", EntryPoint = "write", SetLastError = true)]
        internal static extern nint Write(int fd, [In] byte[] buffer, nuint count);

        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        internal static extern int FSync(int fd);

        [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
        internal static extern int FLock(int fd, int operation);

        [StructLayout(LayoutKind.Explicit, Size = 256)]
        internal struct LinuxStatx
        {
            [FieldOffset(0)] internal uint Mask;
            [FieldOffset(16)] internal uint LinkCount;
            [FieldOffset(28)] internal ushort Mode;
            [FieldOffset(32)] internal ulong Inode;
            [FieldOffset(40)] internal ulong Size;
            [FieldOffset(136)] internal uint DeviceMajor;
            [FieldOffset(140)] internal uint DeviceMinor;
        }
    }

}

internal sealed class UnixNativeCallException(int error, string operation, bool unsupported = false)
    : IOException(operation)
{
    internal int Error { get; } = error;
    internal bool Unsupported { get; } = unsupported;
}
