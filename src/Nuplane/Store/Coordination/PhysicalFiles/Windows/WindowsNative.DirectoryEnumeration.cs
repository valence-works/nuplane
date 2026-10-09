using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination.PhysicalFiles.Windows;

internal static partial class WindowsNative
{
    private const int FileNamesInformationClass = 12;
    private const int FileNamesInformationHeaderSize = 12;
    private const int MaximumNtfsNameBytes = 255 * sizeof(char);
    private const int DirectoryQueryBufferSize = 64 * 1024;
    private const int StatusNoMoreFiles = unchecked((int)0x80000006);
    private const int StatusNoSuchFile = unchecked((int)0xC000000F);

    private static readonly Encoding StrictDirectoryNameEncoding = new UnicodeEncoding(
        bigEndian: false,
        byteOrderMark: false,
        throwOnInvalidBytes: true);

    internal static IReadOnlyList<string> EnumerateDirectoryNames(SafeFileHandle directory, int maximumEntries)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (maximumEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));

        var names = new List<string>(Math.Min(maximumEntries, 1024));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var buffer = new byte[DirectoryQueryBufferSize];
        var restartScan = true;

        while (true)
        {
            var isFirstQuery = restartScan;
            var result = QueryDirectory(directory, buffer, restartScan);
            restartScan = false;
            if (result.Status == StatusPending || result.IoStatus == StatusPending)
            {
                throw new WindowsNativeCallException(
                    "Synchronous NtQueryDirectoryFile unexpectedly returned STATUS_PENDING.",
                    unsupported: true);
            }

            if (IsDirectoryEnumerationEnd(result.Status, result.IoStatus, result.BytesReturned, isFirstQuery))
                break;

            if (result.Status < 0)
                throw new WindowsNativeCallException("NtQueryDirectoryFile could not enumerate the held directory.", ntStatus: result.Status);
            if (result.IoStatus < 0)
                throw new WindowsNativeCallException("NtQueryDirectoryFile reported a failed completion status.", ntStatus: result.IoStatus);
            if (result.Status != 0 || result.IoStatus != 0)
            {
                throw new WindowsNativeCallException(
                    "NtQueryDirectoryFile returned an unexpected informational status.",
                    ntStatus: result.Status != 0 ? result.Status : result.IoStatus,
                    unsupported: true);
            }
            if (result.BytesReturned <= 0 || result.BytesReturned > buffer.Length)
                throw new WindowsNativeCallException("NtQueryDirectoryFile returned an invalid directory-record length.", unsupported: true);

            var batch = ParseDirectoryNamesInformation(buffer.AsSpan(0, result.BytesReturned), maximumEntries);
            foreach (var name in batch)
            {
                if (names.Count >= maximumEntries)
                    throw new WindowsNativeCallException("The held directory contains more child names than the requested bound.");
                if (!seen.Add(name))
                    throw new WindowsNativeCallException("NtQueryDirectoryFile returned a duplicate child name.");
                names.Add(name);
            }
        }

        names.Sort(StringComparer.Ordinal);
        return names.ToArray();
    }

    internal static bool IsDirectoryEnumerationEnd(int status, int ioStatus, int bytesReturned, bool isFirstQuery)
    {
        // MS-FSA permits NO_SUCH_FILE for an empty initial query. A later NO_SUCH_FILE
        // is not evidence of normal completion, and contradictory status/data always refuse.
        var terminal = status == StatusNoMoreFiles || ioStatus == StatusNoMoreFiles
            ? StatusNoMoreFiles
            : isFirstQuery && (status == StatusNoSuchFile || ioStatus == StatusNoSuchFile)
                ? StatusNoSuchFile
                : 0;
        if (terminal == 0)
            return false;
        if (bytesReturned != 0 || (status != 0 && status != terminal) || (ioStatus != 0 && ioStatus != terminal))
        {
            throw new WindowsNativeCallException(
                "NtQueryDirectoryFile returned inconsistent end-of-scan status or data.",
                unsupported: true);
        }
        return true;
    }

    internal static IReadOnlyList<string> ParseDirectoryNamesInformation(ReadOnlySpan<byte> buffer, int maximumEntries)
    {
        if (maximumEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        while (true)
        {
            var remaining = buffer.Length - offset;
            if (remaining < FileNamesInformationHeaderSize)
                throw new WindowsNativeCallException("A FILE_NAMES_INFORMATION header was truncated.");

            var record = buffer[offset..];
            var nextEntryOffset = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);
            if (nameLength == 0 || (nameLength & 1) != 0 || nameLength > MaximumNtfsNameBytes)
                throw new WindowsNativeCallException("A FILE_NAMES_INFORMATION name length is invalid for local NTFS.");

            var nameByteCount = checked((int)nameLength);
            var nameEnd = checked(FileNamesInformationHeaderSize + nameByteCount);
            if (nameEnd > remaining)
                throw new WindowsNativeCallException("A FILE_NAMES_INFORMATION name exceeded its returned record buffer.");

            string name;
            try
            {
                name = StrictDirectoryNameEncoding.GetString(record.Slice(FileNamesInformationHeaderSize, nameByteCount));
                if (name.Length > 255)
                    throw new ArgumentException("A native child name exceeds the qualified NTFS component bound.");
                if (name is not ("." or ".."))
                    PhysicalStoreNames.ValidateSingleComponent(name);
            }
            catch (DecoderFallbackException exception)
            {
                throw new WindowsNativeCallException($"A FILE_NAMES_INFORMATION name is not valid UTF-16: {exception.Message}");
            }
            catch (ArgumentException exception)
            {
                throw new WindowsNativeCallException($"A FILE_NAMES_INFORMATION name is not a valid single component: {exception.Message}");
            }

            if (name is not ("." or ".."))
            {
                if (!seen.Add(name))
                    throw new WindowsNativeCallException("A FILE_NAMES_INFORMATION batch contains a duplicate child name.");
                if (names.Count >= maximumEntries)
                    throw new WindowsNativeCallException("A FILE_NAMES_INFORMATION batch exceeded the requested entry bound.");
                names.Add(name);
            }

            if (nextEntryOffset == 0)
            {
                if (remaining - nameEnd > 7)
                    throw new WindowsNativeCallException("A final FILE_NAMES_INFORMATION record has unexplained trailing data.");
                return names;
            }

            var minimumNextOffset = checked((uint)nameEnd);
            if (nextEntryOffset < minimumNextOffset ||
                (nextEntryOffset & 3) != 0 ||
                nextEntryOffset - minimumNextOffset > 7 ||
                nextEntryOffset > remaining)
            {
                throw new WindowsNativeCallException("A FILE_NAMES_INFORMATION next-record offset is invalid or truncated.");
            }

            offset = checked(offset + (int)nextEntryOffset);
        }
    }

    private static DirectoryQueryResult QueryDirectory(SafeFileHandle directory, byte[] buffer, bool restartScan)
    {
        var addedReference = false;
        directory.DangerousAddRef(ref addedReference);
        var pin = default(GCHandle);
        try
        {
            pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            var status = NtQueryDirectoryFile(
                directory.DangerousGetHandle(),
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                out var ioStatus,
                pin.AddrOfPinnedObject(),
                checked((uint)buffer.Length),
                FileNamesInformationClass,
                false,
                IntPtr.Zero,
                restartScan);
            var completionStatus = unchecked((int)ioStatus.Status.ToInt64());
            var information = ioStatus.Information.ToInt64();
            if (information < 0 || information > buffer.Length)
                throw new WindowsNativeCallException("NtQueryDirectoryFile returned an invalid IO_STATUS_BLOCK byte count.", unsupported: true);

            return new DirectoryQueryResult(status, completionStatus, checked((int)information));
        }
        finally
        {
            if (pin.IsAllocated)
                pin.Free();
            if (addedReference)
                directory.DangerousRelease();
        }
    }

    private readonly record struct DirectoryQueryResult(int Status, int IoStatus, int BytesReturned);

    [DllImport("ntdll.dll", EntryPoint = "NtQueryDirectoryFile")]
    private static extern int NtQueryDirectoryFile(
        IntPtr fileHandle,
        IntPtr eventHandle,
        IntPtr apcRoutine,
        IntPtr apcContext,
        out IoStatusBlock ioStatusBlock,
        IntPtr fileInformation,
        uint length,
        int fileInformationClass,
        [MarshalAs(UnmanagedType.U1)] bool returnSingleEntry,
        IntPtr fileName,
        [MarshalAs(UnmanagedType.U1)] bool restartScan);
}
