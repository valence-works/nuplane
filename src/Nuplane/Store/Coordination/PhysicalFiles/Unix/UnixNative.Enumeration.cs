using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Nuplane.Store.Coordination.PhysicalFiles.Unix;

internal static partial class UnixNative
{
    private const int MaximumEnumeratedNameBytes = 4096;

    internal static byte[][] EnumerateChildNameBytes(UnixPlatform platform, int parentFd, int maximumEntries)
    {
        if (maximumEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));

        return platform switch
        {
            UnixPlatform.Darwin => EnumerateDarwinChildNameBytes(parentFd, maximumEntries),
            UnixPlatform.Linux => EnumerateLinuxChildNameBytes(parentFd, maximumEntries),
            _ => throw new ArgumentOutOfRangeException(nameof(platform))
        };
    }

    private static byte[][] EnumerateDarwinChildNameBytes(int parentFd, int maximumEntries)
    {
        var result = UnixEnumerationNative.DarwinEnumerateChildNames(
            parentFd,
            (nuint)maximumEntries,
            out var namesPointer,
            out var byteLength,
            out var count);
        if (result != 0)
            throw NativeResultException("enumerate Darwin held-directory child names");

        try
        {
            if (count > (nuint)maximumEntries || byteLength > (nuint)int.MaxValue ||
                (count == 0) != (byteLength == 0) || (byteLength != 0 && namesPointer == IntPtr.Zero))
            {
                throw new UnixNativeCallException(0, "Darwin returned an inconsistent child-name buffer.", unsupported: true);
            }

            var packedNames = new byte[(int)byteLength];
            if (packedNames.Length != 0)
                Marshal.Copy(namesPointer, packedNames, 0, packedNames.Length);

            var names = new byte[(int)count][];
            var offset = 0;
            for (var index = 0; index < names.Length; index++)
            {
                var terminator = packedNames.AsSpan(offset).IndexOf((byte)0);
                if (terminator <= 0)
                    throw new UnixNativeCallException(0, "Darwin returned a truncated or empty child-name record.", unsupported: true);

                names[index] = packedNames.AsSpan(offset, terminator).ToArray();
                offset += terminator + 1;
            }

            if (offset != packedNames.Length)
                throw new UnixNativeCallException(0, "Darwin returned trailing bytes after its child-name records.", unsupported: true);

            return names;
        }
        finally
        {
            if (namesPointer != IntPtr.Zero)
                UnixEnumerationNative.DarwinFree(namesPointer);
        }
    }

    private static byte[][] EnumerateLinuxChildNameBytes(int parentFd, int maximumEntries)
    {
        var parentBefore = StatHandle(UnixPlatform.Linux, parentFd);
        RequireEnumerationDirectory(parentBefore);

        // A fresh open description gives this invocation an independent getdents64 cursor.
        var enumerationFd = UnixNamesNative.LinuxOpenDirectoryStreamAt(parentFd);
        try
        {
            var openedParent = StatHandle(UnixPlatform.Linux, enumerationFd);
            RequireSameDirectory(parentBefore, openedParent, "The independent Linux enumeration stream is not the held parent.");

            var syscallNumber = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => LinuxGetdents64X64,
                Architecture.Arm64 => LinuxGetdents64Arm64,
                _ => throw new UnixNativeCallException(0, "The Linux directory-entry ABI is not qualified for this architecture.", unsupported: true)
            };

            var names = new List<byte[]>();
            var buffer = new byte[32 * 1024];
            while (true)
            {
                var nativeCount = UnixNamesNative.LinuxSyscallGetdents64(
                    syscallNumber,
                    enumerationFd,
                    buffer,
                    (nuint)buffer.Length);
                if (nativeCount < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (IsInterrupted(error))
                        continue;
                    throw new UnixNativeCallException(error, "enumerate Linux held-directory child names");
                }
                if (nativeCount == 0)
                    break;
                if (nativeCount > (nint)buffer.Length)
                    throw new UnixNativeCallException(0, "Linux returned more directory-entry data than the supplied buffer.", unsupported: true);

                var end = checked((int)nativeCount);
                var offset = 0;
                while (offset < end)
                {
                    if (end - offset < 19)
                        throw new UnixNativeCallException(0, "Linux returned a truncated directory-entry header.", unsupported: true);

                    var recordLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 16, sizeof(ushort)));
                    if (recordLength < 20 || recordLength > end - offset)
                        throw new UnixNativeCallException(0, "Linux returned an invalid directory-entry record length.", unsupported: true);

                    var nameArea = buffer.AsSpan(offset + 19, recordLength - 19);
                    var nameLength = nameArea.IndexOf((byte)0);
                    if (nameLength <= 0)
                        throw new UnixNativeCallException(0, "Linux returned an empty or unterminated directory-entry name.", unsupported: true);

                    var name = nameArea[..nameLength];
                    if (!IsDotEntry(name))
                    {
                        if (name.Length >= MaximumEnumeratedNameBytes)
                            throw new UnixNativeCallException(0, "Linux returned an overlong child name.", unsupported: true);
                        if (names.Count == maximumEntries)
                            throw new UnixNativeCallException(0, "The held directory contains more child names than the requested bound.");
                        names.Add(name.ToArray());
                    }

                    offset += recordLength;
                }
            }

            var parentAfter = StatHandle(UnixPlatform.Linux, parentFd);
            RequireSameDirectory(parentBefore, parentAfter, "The held Linux directory changed during child-name enumeration.");
            return names.ToArray();
        }
        finally
        {
            if (UnixNamesNative.LinuxClose(enumerationFd) != 0)
                throw new UnixNativeCallException(Marshal.GetLastPInvokeError(), "close the independent Linux directory enumeration stream");
        }
    }

    private static void RequireEnumerationDirectory(UnixMetadata metadata)
    {
        if ((metadata.Mode & FileTypeMask) != FileTypeDirectory)
            throw new UnixNativeCallException(0, "Child-name enumeration requires a held directory.");
    }

    private static void RequireSameDirectory(UnixMetadata expected, UnixMetadata actual, string message)
    {
        if ((actual.Mode & FileTypeMask) != FileTypeDirectory ||
            expected.Inode != actual.Inode ||
            expected.Device != actual.Device)
        {
            throw new UnixNativeCallException(0, message);
        }
    }

    private static bool IsDotEntry(ReadOnlySpan<byte> name)
        => name.Length == 1 && name[0] == (byte)'.' ||
           name.Length == 2 && name[0] == (byte)'.' && name[1] == (byte)'.';
}

internal static class UnixEnumerationNative
{
    [DllImport("nuplane_store_native", EntryPoint = "nuplane_enumerate_child_names", SetLastError = true)]
    internal static extern int DarwinEnumerateChildNames(
        int parentFd,
        nuint maximumEntries,
        out IntPtr names,
        out nuint byteLength,
        out nuint count);

    [DllImport("libSystem.B.dylib", EntryPoint = "free")]
    internal static extern void DarwinFree(IntPtr value);
}
