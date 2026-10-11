using System.Runtime.InteropServices;
using System.Text;

namespace Nuplane.Store.Coordination.PhysicalFiles.Windows;

internal static partial class WindowsNative
{
    internal const uint DeleteAccess = 0x00010000;
    internal const uint ShareDelete = 0x00000004;
    private const int FileRenameInformation = 10;
    private const int FileRenameInformationEx = 65;
    private const int FileRenameReplaceIfExists = 0x00000001;
    private const int FileRenamePosixSemantics = 0x00000002;
    private static readonly UnicodeEncoding PublicationUtf16 = new(false, false, true);

    internal static bool SupportsPosixRenameReplacement => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299);

    internal static void RenameControlFile(IntPtr file, IntPtr parent, string destinationName, bool replace)
        => RenameAt(file, parent, destinationName, replace, "rename a staged control file");

    internal static void RenameDirectoryNoReplace(IntPtr directory, IntPtr parent, string destinationName)
        => RenameAt(
            directory,
            parent,
            destinationName,
            replace: false,
            operation: "rename a staged directory without replacement");

    private static void RenameAt(IntPtr entry, IntPtr parent, string destinationName, bool replace, string operation)
    {
        // Both FILE_RENAME_INFORMATION and FILE_RENAME_INFORMATION_EX share the qualified
        // Windows x64 layout: the first eight bytes hold BOOLEAN/padding or Flags/Reserved,
        // followed by HANDLE at 8, ULONG byte length at 16, and WCHAR filename at 20.
        var usePosixReplacement = replace && SupportsPosixRenameReplacement;
        var name = PublicationUtf16.GetBytes(destinationName);
        var length = checked(24 + name.Length);
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.Copy(new byte[length], 0, buffer, length);
            if (usePosixReplacement)
            {
                Marshal.WriteInt32(
                    buffer,
                    0,
                    FileRenameReplaceIfExists | FileRenamePosixSemantics);
            }
            else
                Marshal.WriteByte(buffer, replace ? (byte)1 : (byte)0);
            Marshal.WriteIntPtr(buffer, 8, parent);
            Marshal.WriteInt32(buffer, 16, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(buffer, 20), name.Length);
            SetPublicationInformation(
                entry,
                buffer,
                length,
                usePosixReplacement ? FileRenameInformationEx : FileRenameInformation,
                operation);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static void RemoveControlFile(IntPtr file)
    {
        var buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, 1); // FILE_DISPOSITION_INFORMATION.DeleteFile.
            SetPublicationInformation(file, buffer, 1, informationClass: 13, "remove a transaction-control file");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void SetPublicationInformation(IntPtr entry, IntPtr buffer, int length, int informationClass, string operation)
    {
        var status = NtSetInformationFile(entry, out var ioStatus, buffer, checked((uint)length), informationClass);
        var ioStatusCode = unchecked((int)ioStatus.Status.ToInt64());
        if (status == StatusPending || ioStatusCode == StatusPending)
            throw new WindowsNativeCallException("Synchronous native publication returned STATUS_PENDING.", unsupported: true);
        if (status < 0 || ioStatusCode < 0)
            throw new WindowsNativeCallException(operation, ntStatus: status < 0 ? status : ioStatusCode);
    }

    [DllImport("ntdll.dll", EntryPoint = "NtSetInformationFile")]
    private static extern int NtSetInformationFile(
        IntPtr file,
        out IoStatusBlock ioStatus,
        IntPtr information,
        uint length,
        int informationClass);
}
