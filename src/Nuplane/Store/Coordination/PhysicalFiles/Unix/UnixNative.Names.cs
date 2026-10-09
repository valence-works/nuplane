using System.Runtime.InteropServices;
using System.Text;

namespace Nuplane.Store.Coordination.PhysicalFiles.Unix;

internal readonly record struct UnixNameProfile(string ProfileId, bool CaseSensitive, bool NormalizationInsensitive);

internal static partial class UnixNative
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const int LinuxExt4Magic = 0xEF53;
    private const int LinuxFsCasefoldFlag = 0x40000000;
    private const ulong LinuxGetFlagsRequest = 0x80086601;
    private const long LinuxGetdents64X64 = 217;
    private const long LinuxGetdents64Arm64 = 61;

    internal static UnixNameProfile GetNameProfile(UnixPlatform platform, int directoryFd)
    {
        if (platform == UnixPlatform.Darwin)
        {
            var result = UnixNamesNative.DarwinGetNameProfile(directoryFd, out var caseSensitive, out var normalizationInsensitive);
            if (result != 0)
                throw NativeResultException("inspect Darwin directory name semantics");

            return new UnixNameProfile("darwin-apfs-v1", caseSensitive != 0, normalizationInsensitive != 0);
        }

        if (platform != UnixPlatform.Linux)
            throw new ArgumentOutOfRangeException(nameof(platform));

        if (UnixNamesNative.LinuxFStatFs(directoryFd, out var fileSystem) != 0)
            throw NativeResultException("identify Linux directory filesystem");
        if (fileSystem.FileSystemType != LinuxExt4Magic)
            throw new UnixNativeCallException(0, "The Linux name provider currently qualifies ext4 only.", unsupported: true);

        // FS_IOC_GETFLAGS is defined with sizeof(long) in its request encoding on
        // the supported 64-bit Linux ABIs. Keep the pointed-to storage native-sized
        // even though current ext4 kernels expose the flag bits in an int-sized field.
        long nativeFlags = 0;
        var flagsResult = UnixNamesNative.LinuxIoctlGetFlags(directoryFd, LinuxGetFlagsRequest, ref nativeFlags);
        if (flagsResult != 0)
            throw NativeResultException("inspect ext4 directory casefold semantics");

        var isCasefold = (((uint)nativeFlags) & LinuxFsCasefoldFlag) != 0;
        return new UnixNameProfile(
            isCasefold ? "linux-ext4-casefold-v1" : "linux-ext4-sensitive-v1",
            CaseSensitive: !isCasefold,
            NormalizationInsensitive: isCasefold);
    }

    internal static byte[] FindEntryName(UnixPlatform platform, int parentFd, int fileFd, int maximumNameBytes)
    {
        if (maximumNameBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumNameBytes));

        return platform switch
        {
            UnixPlatform.Darwin => FindDarwinEntryName(parentFd, fileFd, maximumNameBytes),
            UnixPlatform.Linux => FindLinuxEntryName(parentFd, fileFd, maximumNameBytes),
            _ => throw new ArgumentOutOfRangeException(nameof(platform))
        };
    }

    private static byte[] FindDarwinEntryName(int parentFd, int fileFd, int maximumNameBytes)
    {
        var buffer = new byte[maximumNameBytes];
        var result = UnixNamesNative.DarwinFindEntryName(parentFd, fileFd, buffer, (nuint)buffer.Length);
        if (result != 0)
            throw NativeResultException("enumerate Darwin held-parent entry names");

        var terminator = Array.IndexOf(buffer, (byte)0);
        if (terminator <= 0)
            throw new UnixNativeCallException(0, "Darwin returned an empty or unterminated child name.", unsupported: true);
        return buffer.AsSpan(0, terminator).ToArray();
    }

    private static byte[] FindLinuxEntryName(int parentFd, int fileFd, int maximumNameBytes)
    {
        var parentBefore = StatHandle(UnixPlatform.Linux, parentFd);
        var fileBefore = StatHandle(UnixPlatform.Linux, fileFd);
        RequireNameTarget(parentBefore, fileBefore);

        // A fresh open description gives getdents64 an independent stream offset. dup() would share it.
        var enumerationFd = UnixNamesNative.LinuxOpenDirectoryStreamAt(parentFd);

        string? matchedName = null;
        try
        {
            var openedParent = StatHandle(UnixPlatform.Linux, enumerationFd);
            if (openedParent.Inode != parentBefore.Inode || openedParent.Device != parentBefore.Device)
                throw new UnixNativeCallException(0, "The held parent changed before name enumeration.");

            var buffer = new byte[32 * 1024];
            var syscallNumber = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => LinuxGetdents64X64,
                Architecture.Arm64 => LinuxGetdents64Arm64,
                _ => throw new UnixNativeCallException(0, "The Linux directory-entry ABI is not qualified for this architecture.", unsupported: true)
            };

            while (true)
            {
                var count = UnixNamesNative.LinuxSyscallGetdents64(syscallNumber, enumerationFd, buffer, (nuint)buffer.Length);
                if (count < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (IsInterrupted(error))
                        continue;
                    throw new UnixNativeCallException(error, "enumerate Linux held-parent entries");
                }
                if (count == 0)
                    break;

                var end = checked((int)count);
                var offset = 0;
                while (offset < end)
                {
                    if (end - offset < 19)
                        throw new UnixNativeCallException(0, "Linux returned a truncated directory-entry header.", unsupported: true);

                    var inode = BitConverter.ToUInt64(buffer, offset);
                    var recordLength = BitConverter.ToUInt16(buffer, offset + 16);
                    if (recordLength < 20 || recordLength > end - offset)
                        throw new UnixNativeCallException(0, "Linux returned an invalid directory-entry record length.", unsupported: true);

                    if (inode == fileBefore.Inode)
                    {
                        var nameArea = buffer.AsSpan(offset + 19, recordLength - 19);
                        var nameLength = nameArea.IndexOf((byte)0);
                        if (nameLength < 0)
                            throw new UnixNativeCallException(0, "Linux returned an unterminated directory-entry name.", unsupported: true);
                        if (nameLength > 0)
                        {
                            var nameBytes = nameArea[..nameLength];
                            var name = StrictUtf8.GetString(nameBytes);
                            var candidate = StatAt(UnixPlatform.Linux, parentFd, name);
                            if (candidate.Status != UnixStatResultStatus.Success)
                                throw new UnixNativeCallException(candidate.Error, "inspect a Linux directory entry whose inode matches the held file");
                            if (candidate.Metadata.Inode == fileBefore.Inode &&
                                candidate.Metadata.Device == fileBefore.Device &&
                                candidate.Metadata.LinkCount == 1 &&
                                (candidate.Metadata.Mode & FileTypeMask) == FileTypeRegular)
                            {
                                if (matchedName is not null)
                                    throw new UnixNativeCallException(0, "The held file has multiple matching directory entries.");
                                matchedName = name;
                            }
                        }
                    }

                    offset += recordLength;
                }
            }

            var fileAfter = StatHandle(UnixPlatform.Linux, fileFd);
            var parentAfter = StatHandle(UnixPlatform.Linux, parentFd);
            if (fileAfter.Inode != fileBefore.Inode || fileAfter.Device != fileBefore.Device || fileAfter.LinkCount != 1 ||
                parentAfter.Inode != parentBefore.Inode || parentAfter.Device != parentBefore.Device)
            {
                throw new UnixNativeCallException(0, "The held file or parent changed during name enumeration.");
            }

            if (matchedName is null)
                throw new UnixNativeCallException(0, "No unique parent entry matched the held file identity.");

            var finalEntry = StatAt(UnixPlatform.Linux, parentFd, matchedName);
            if (finalEntry.Status != UnixStatResultStatus.Success ||
                finalEntry.Metadata.Inode != fileBefore.Inode ||
                finalEntry.Metadata.Device != fileBefore.Device ||
                finalEntry.Metadata.LinkCount != 1 ||
                (finalEntry.Metadata.Mode & FileTypeMask) != FileTypeRegular)
            {
                throw new UnixNativeCallException(0, "The canonical Linux directory entry changed during enumeration.");
            }

            var result = StrictUtf8.GetBytes(matchedName);
            if (result.Length == 0 || result.Length >= maximumNameBytes)
                throw new UnixNativeCallException(0, "The canonical Linux directory-entry name exceeds the supported bound.", unsupported: true);
            return result;
        }
        catch (DecoderFallbackException exception)
        {
            throw new UnixNativeCallException(0, $"A Linux directory entry name was not valid UTF-8: {exception.Message}", unsupported: true);
        }
        finally
        {
            _ = UnixNamesNative.LinuxClose(enumerationFd);
        }
    }

    private static void RequireNameTarget(UnixMetadata parent, UnixMetadata file)
    {
        if ((parent.Mode & FileTypeMask) != FileTypeDirectory ||
            (file.Mode & FileTypeMask) != FileTypeRegular ||
            file.LinkCount != 1 ||
            parent.Device != file.Device)
        {
            throw new UnixNativeCallException(0, "Canonical name observation requires a same-volume directory and single-link regular file.");
        }
    }

}

internal static class UnixNamesNative
{
    [DllImport("nuplane_store_native", EntryPoint = "nuplane_apfs_name_profile", SetLastError = true)]
    internal static extern int DarwinGetNameProfile(int directoryFd, out int caseSensitive, out int normalizationInsensitive);

    [DllImport("nuplane_store_native", EntryPoint = "nuplane_find_entry_name", SetLastError = true)]
    internal static extern int DarwinFindEntryName(int parentFd, int fileFd, [Out] byte[] name, nuint capacity);

    [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    internal static extern int LinuxFStatFs(int fd, out LinuxStatFs statfs);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    internal static extern int LinuxIoctlGetFlags(int fd, ulong request, ref long flags);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    internal static extern nint LinuxSyscallGetdents64(long number, int fd, [Out] byte[] buffer, nuint count);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    internal static extern int LinuxClose(int fd);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int LinuxOpenAt(int parentFd, string name, int flags, uint mode);

    internal static int LinuxOpenDirectoryStreamAt(int parentFd)
    {
        var fd = LinuxOpenAt(parentFd, ".", UnixNative.LinuxDirectoryStreamFlags, 0);
        return fd < 0
            ? throw new UnixNativeCallException(Marshal.GetLastPInvokeError(), "open an independent held-directory enumeration handle")
            : fd;
    }

    [StructLayout(LayoutKind.Explicit, Size = 120)]
    internal struct LinuxStatFs
    {
        [FieldOffset(0)] internal long FileSystemType;
    }
}
