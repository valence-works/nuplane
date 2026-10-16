using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination.PhysicalFiles.Windows;

internal static partial class WindowsNative
{
    private const int FileIdExtdBothDirectoryInformationClass = 63;
    private const int StateDestinationDirectoryEntryHeaderSize = 114;
    private const int StateDestinationDirectoryEntryMaximumNameBytes = 255 * sizeof(char);
    private const int StateDestinationDirectoryEntryFileIdOffset = 72;
    private const int StateDestinationDirectoryEntryShortNameLengthOffset = 88;
    private const int StateDestinationDirectoryEntryShortNameOffset = 90;
    private const int StateDestinationDirectoryEntryFileNameOffset = 114;

    private static readonly Encoding StrictStateDestinationNameEncoding = new UnicodeEncoding(
        bigEndian: false,
        byteOrderMark: false,
        throwOnInvalidBytes: true);

    internal static StateDestinationDirectoryEntry QueryStateDestinationDirectoryEntry(
        SafeFileHandle directory,
        string exactName)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(exactName);
        PhysicalStoreNames.ValidateSingleComponent(exactName);
        if (exactName.Length > 255 || ContainsDirectoryWildcard(exactName))
        {
            throw new WindowsNativeCallException(
                "The exact directory-entry filter exceeds the NTFS component limit or contains wildcard syntax.",
                unsupported: true);
        }

        var nameByteLength = checked(exactName.Length * sizeof(char));
        var nameBuffer = Marshal.StringToHGlobalUni(exactName);
        var unicodeStringPointer = IntPtr.Zero;
        try
        {
            unicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(
                new UnicodeString
                {
                    Length = checked((ushort)nameByteLength),
                    MaximumLength = checked((ushort)nameByteLength),
                    Buffer = nameBuffer
                },
                unicodeStringPointer,
                fDeleteOld: false);

            var buffer = new byte[DirectoryQueryBufferSize];
            var addedReference = false;
            var pin = default(GCHandle);
            try
            {
                directory.DangerousAddRef(ref addedReference);
                pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                var status = NtQueryDirectoryFile(
                    directory.DangerousGetHandle(),
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out var ioStatus,
                    pin.AddrOfPinnedObject(),
                    checked((uint)buffer.Length),
                    FileIdExtdBothDirectoryInformationClass,
                    returnSingleEntry: true,
                    fileName: unicodeStringPointer,
                    restartScan: true);
                var completionStatus = unchecked((int)ioStatus.Status.ToInt64());
                var byteCount = ioStatus.Information.ToInt64();
                if (byteCount < 0 || byteCount > buffer.Length)
                    throw new WindowsNativeCallException("NtQueryDirectoryFile returned an invalid extended-entry byte count.", unsupported: true);

                var bytesReturned = checked((int)byteCount);
                ThrowIfExactDirectoryQueryAbsent(status, completionStatus, bytesReturned);
                if (status == StatusPending || completionStatus == StatusPending)
                    throw new WindowsNativeCallException("Synchronous exact NtQueryDirectoryFile unexpectedly returned STATUS_PENDING.", unsupported: true);
                if (status < 0)
                    throw new WindowsNativeCallException("NtQueryDirectoryFile could not query the exact state-destination entry.", ntStatus: status);
                if (completionStatus < 0)
                    throw new WindowsNativeCallException("NtQueryDirectoryFile reported a failed exact-entry completion status.", ntStatus: completionStatus);
                if (status != 0 || completionStatus != 0)
                {
                    throw new WindowsNativeCallException(
                        "NtQueryDirectoryFile returned an unexpected informational exact-entry status.",
                        ntStatus: status != 0 ? status : completionStatus,
                        unsupported: true);
                }
                if (bytesReturned == 0 || bytesReturned > buffer.Length)
                    throw new WindowsNativeCallException("NtQueryDirectoryFile returned no exact-entry record.", unsupported: true);

                return ParseStateDestinationDirectoryEntry(buffer.AsSpan(0, bytesReturned));
            }
            finally
            {
                if (pin.IsAllocated)
                    pin.Free();
                if (addedReference)
                    directory.DangerousRelease();
            }
        }
        finally
        {
            if (unicodeStringPointer != IntPtr.Zero)
                Marshal.FreeHGlobal(unicodeStringPointer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    internal static StateDestinationDirectoryEntry ParseStateDestinationDirectoryEntry(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < StateDestinationDirectoryEntryHeaderSize)
            throw new WindowsNativeCallException("A FILE_ID_EXTD_BOTH_DIR_INFORMATION header was truncated.");

        var nextEntryOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        var fileNameLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer[60..]);
        var reparsePointTag = BinaryPrimitives.ReadUInt32LittleEndian(buffer[68..]);
        var fileIdLow = BinaryPrimitives.ReadUInt64LittleEndian(buffer[StateDestinationDirectoryEntryFileIdOffset..]);
        var fileIdHigh = BinaryPrimitives.ReadUInt64LittleEndian(buffer[(StateDestinationDirectoryEntryFileIdOffset + sizeof(ulong))..]);
        var shortNameLength = buffer[StateDestinationDirectoryEntryShortNameLengthOffset];

        if (nextEntryOffset != 0)
            throw new WindowsNativeCallException("An exact filtered directory query returned more than one entry record.");
        if (fileNameLength == 0 || (fileNameLength & 1) != 0 || fileNameLength > StateDestinationDirectoryEntryMaximumNameBytes)
            throw new WindowsNativeCallException("The extended directory record has an invalid long-name length.");
        if (shortNameLength > 12 * sizeof(char) || (shortNameLength & 1) != 0)
            throw new WindowsNativeCallException("The extended directory record has an invalid short-name length.");
        if (fileIdLow == 0 && fileIdHigh == 0)
            throw new WindowsNativeCallException("The extended directory record has no usable 128-bit file reference number.");

        var fileNameEnd = checked(StateDestinationDirectoryEntryFileNameOffset + checked((int)fileNameLength));
        if (fileNameEnd > buffer.Length || buffer.Length - fileNameEnd > 7)
            throw new WindowsNativeCallException("The extended directory record name exceeded its returned buffer or had unexplained trailing data.");

        string fileName;
        string shortName;
        try
        {
            fileName = StrictStateDestinationNameEncoding.GetString(
                buffer.Slice(StateDestinationDirectoryEntryFileNameOffset, checked((int)fileNameLength)));
            shortName = shortNameLength == 0
                ? string.Empty
                : StrictStateDestinationNameEncoding.GetString(
                    buffer.Slice(StateDestinationDirectoryEntryShortNameOffset, shortNameLength));
            PhysicalStoreNames.ValidateSingleComponent(fileName);
            if (shortName.Length > 0)
                PhysicalStoreNames.ValidateSingleComponent(shortName);
            if (ContainsDirectoryWildcard(fileName) || ContainsDirectoryWildcard(shortName))
                throw new ArgumentException("An extended directory record contains a name that cannot be safely used as an exact directory filter.");
        }
        catch (DecoderFallbackException exception)
        {
            throw new WindowsNativeCallException($"An extended directory record name is not valid UTF-16: {exception.Message}");
        }
        catch (ArgumentException exception)
        {
            throw new WindowsNativeCallException($"An extended directory record name is not one valid component: {exception.Message}");
        }

        var fileAttributes = BinaryPrimitives.ReadUInt32LittleEndian(buffer[56..]);
        var hasReparseAttribute = (fileAttributes & FileAttributeReparsePoint) != 0;
        if (hasReparseAttribute != (reparsePointTag != 0))
            throw new WindowsNativeCallException("The extended directory record has inconsistent reparse attributes and tag.");

        var endOfFile = BinaryPrimitives.ReadInt64LittleEndian(buffer[40..]);
        if (endOfFile < 0)
            throw new WindowsNativeCallException("The extended directory record has a negative file length.");

        return new StateDestinationDirectoryEntry(
            fileIdLow,
            fileIdHigh,
            fileAttributes,
            endOfFile,
            reparsePointTag,
            shortName,
            fileName);
    }

    private static bool ContainsDirectoryWildcard(string name)
        => name.IndexOfAny(['*', '?', '<', '>', '"']) >= 0;

    private static void ThrowIfExactDirectoryQueryAbsent(int status, int ioStatus, int bytesReturned)
    {
        var terminal = status == StatusNoMoreFiles || ioStatus == StatusNoMoreFiles
            ? StatusNoMoreFiles
            : status == StatusNoSuchFile || ioStatus == StatusNoSuchFile
                ? StatusNoSuchFile
                : 0;
        if (terminal == 0)
            return;
        if (bytesReturned != 0 ||
            (status != 0 && status != terminal) ||
            (ioStatus != 0 && ioStatus != terminal))
        {
            throw new WindowsNativeCallException(
                "NtQueryDirectoryFile returned inconsistent exact-entry absence status or data.",
                unsupported: true);
        }

        throw new WindowsNativeCallException(
            "NtQueryDirectoryFile found no entry matching the exact state-destination component.",
            ntStatus: terminal);
    }

    internal readonly record struct StateDestinationDirectoryEntry(
        ulong FileIdLow,
        ulong FileIdHigh,
        uint FileAttributes,
        long EndOfFile,
        uint ReparsePointTag,
        string ShortName,
        string FileName);
}
