using System.Runtime.InteropServices;

namespace Nuplane.Store.Coordination.PhysicalFiles.Unix;

internal static partial class UnixNative
{
    private const uint DarwinRenameExclusive = 0x00000004;
    private const uint LinuxRenameNoReplace = 0x00000001;

    internal static int PublishReplaceAt(UnixPlatform platform, int parentFd, string stagedName, string destinationName)
    {
        var result = platform == UnixPlatform.Darwin
            ? DarwinPublication.RenameAtX(parentFd, stagedName, parentFd, destinationName, flags: 0)
            : LinuxPublication.RenameAt(parentFd, stagedName, parentFd, destinationName);
        // Never repeat a namespace mutation after an uncertain error, including EINTR.
        // The pending protocol must reopen and classify actual evidence before retrying.
        return result == 0 ? 0 : throw NativeResultException("atomically replace a held-parent control file");
    }

    internal static int PublishNoReplaceAt(UnixPlatform platform, int parentFd, string stagedName, string destinationName)
    {
        var result = platform == UnixPlatform.Darwin
            ? DarwinPublication.RenameAtX(parentFd, stagedName, parentFd, destinationName, DarwinRenameExclusive)
            : LinuxPublication.RenameAt2(parentFd, stagedName, parentFd, destinationName, LinuxRenameNoReplace);
        if (result == 0)
            return 0;

        var error = Marshal.GetLastPInvokeError();
        // These flags are the native no-overwrite guarantee. An unsupported flag or volume
        // must refuse publication instead of falling back to an overwriting rename.
        if (error == 22)
            throw new UnixNativeCallException(error, "the filesystem does not support atomic no-replace publication", unsupported: true);

        throw new UnixNativeCallException(error, "atomically publish a held-parent control file without replacement");
    }

    internal static int UnlinkFileAt(UnixPlatform platform, int parentFd, string singleName)
    {
        var result = platform == UnixPlatform.Darwin
            ? DarwinPublication.UnlinkAt(parentFd, singleName, flags: 0)
            : LinuxPublication.UnlinkAt(parentFd, singleName, flags: 0);
        return result == 0 ? 0 : throw NativeResultException("remove a held-parent transaction control file");
    }

    private static class DarwinPublication
    {
        [DllImport("libSystem.B.dylib", EntryPoint = "renameatx_np", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int RenameAtX(int fromDirectoryFd, string fromName, int toDirectoryFd, string toName, uint flags);

        [DllImport("libSystem.B.dylib", EntryPoint = "unlinkat", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int UnlinkAt(int directoryFd, string name, int flags);
    }

    private static class LinuxPublication
    {
        [DllImport("libc", EntryPoint = "renameat", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int RenameAt(int fromDirectoryFd, string fromName, int toDirectoryFd, string toName);

        [DllImport("libc", EntryPoint = "renameat2", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int RenameAt2(int fromDirectoryFd, string fromName, int toDirectoryFd, string toName, uint flags);

        [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int UnlinkAt(int directoryFd, string name, int flags);
    }
}
