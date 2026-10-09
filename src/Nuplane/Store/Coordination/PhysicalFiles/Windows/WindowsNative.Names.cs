using System.Runtime.InteropServices;
using System.Text;

namespace Nuplane.Store.Coordination.PhysicalFiles.Windows;

internal static partial class WindowsNative
{
    internal const uint ObjectCaseInsensitive = 0x00000040;
    internal const int FileCaseSensitiveInfoClass = 23;
    internal const uint FileCaseSensitiveDirectory = 0x00000001;
    internal const int ErrorInvalidParameter = 87;
    internal const uint FileNameNormalized = 0x00000000;
    internal const uint VolumeNameGuid = 0x00000001;
    private const int MaximumNormalizedPathCharacters = 32768;

    internal static bool QueryDirectoryCaseSensitive(IntPtr directoryHandle)
    {
        FileCaseSensitiveInformation information;
        if (!GetFileInformationByHandleEx(
                directoryHandle,
                FileCaseSensitiveInfoClass,
                out information,
                checked((uint)Marshal.SizeOf<FileCaseSensitiveInformation>())))
        {
            var error = Marshal.GetLastPInvokeError();
            var unsupported = error is ErrorInvalidParameter or ErrorNotSupported or ErrorInvalidFunction;
            throw new WindowsNativeCallException(
                "Windows could not report the held directory's case-sensitivity profile.",
                errorCode: error,
                unsupported: unsupported);
        }

        if ((information.Flags & ~FileCaseSensitiveDirectory) != 0)
            throw new WindowsNativeCallException("Windows returned unknown directory case-sensitivity flags.", unsupported: true);

        return (information.Flags & FileCaseSensitiveDirectory) != 0;
    }

    internal static string GetNormalizedVolumePath(IntPtr fileHandle)
    {
        var capacity = 512;
        while (capacity <= MaximumNormalizedPathCharacters)
        {
            var path = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(
                fileHandle,
                path,
                checked((uint)capacity),
                FileNameNormalized | VolumeNameGuid);
            if (length == 0)
                throw new WindowsNativeCallException("GetFinalPathNameByHandleW could not observe the held file spelling.", errorCode: Marshal.GetLastPInvokeError());
            if (length < capacity)
                return path.ToString();
            if (length >= MaximumNormalizedPathCharacters)
                throw new WindowsNativeCallException("The normalized volume path exceeds the supported native path bound.", unsupported: true);

            capacity = checked((int)length + 1);
        }

        throw new WindowsNativeCallException("The normalized volume path exceeds the supported native path bound.", unsupported: true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileCaseSensitiveInformation
    {
        internal uint Flags;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        IntPtr file,
        int fileInformationClass,
        out FileCaseSensitiveInformation information,
        uint bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        IntPtr file,
        StringBuilder filePath,
        uint filePathSize,
        uint flags);
}
