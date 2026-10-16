using Microsoft.Win32.SafeHandles;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Windows;

namespace Nuplane.Store.Coordination;

internal sealed partial class WindowsPhysicalStoreFileSystem
{
    /// <summary>Observes the exact requested destination entry using a filtered native directory query and no-follow opens.</summary>
    /// <param name="parent">The already held native parent directory.</param>
    /// <param name="requestedName">The exact validated component supplied by the path resolver.</param>
    /// <param name="expectedParentIdentity">The parent identity captured by the caller's retained path resolution.</param>
    /// <param name="expectedNameSemantics">The parent name profile captured by the caller's retained path resolution.</param>
    /// <returns>The exact entry's stored long and short names with repeated no-follow identity evidence.</returns>
    /// <exception cref="PackageStoreAdmissionException">The parent, name profile, filtered entry, or repeated identity evidence is unsafe or unavailable.</exception>
    internal WindowsStateDestinationEntryObservation ObserveStateDestinationEntryNoFollow(
        PhysicalStoreDirectoryHandle parent,
        string requestedName,
        PhysicalFileIdentity expectedParentIdentity,
        PhysicalStoreNameSemantics expectedNameSemantics)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(expectedParentIdentity);
        ArgumentNullException.ThrowIfNull(expectedNameSemantics);
        ValidateName(requestedName);
        RequireSupportedPlatform();

        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var parentState = GetState(parent);
        if (parentState.Identity != expectedParentIdentity)
            throw Unknown("The retained state-destination parent differs from the selected parent identity.");

        try
        {
            var parentHandle = parentLease.DangerousHandle;
            var parentNativeBefore = WindowsNative.QueryEntry(parentHandle);
            var parentBefore = ToEntryInfo(parentNativeBefore);
            RequireExpectedObservationParent(parentBefore, expectedParentIdentity, expectedNameSemantics, parentHandle);
            var profileBefore = ObserveNameSemantics(parentHandle);

            var firstRecord = QueryStateDestinationEntryOnFreshParent(
                parentHandle, parentNativeBefore, parentBefore, profileBefore, requestedName);
            RequireSelectedDirectoryRecord(firstRecord, requestedName, profileBefore);
            using var firstEntryHandle = OpenStateDestinationEntryNoFollow(parentHandle, requestedName);
            var firstNativeEntry = WindowsNative.QueryEntry(firstEntryHandle.DangerousGetHandle());
            var firstEntry = ToEntryInfo(firstNativeEntry);
            RequireSameVolume(parentBefore.Identity, firstEntry.Identity);
            RequireDirectoryRecordMatches(firstRecord, firstNativeEntry);

            var secondRecord = QueryStateDestinationEntryOnFreshParent(
                parentHandle, parentNativeBefore, parentBefore, profileBefore, requestedName);
            RequireSelectedDirectoryRecord(secondRecord, requestedName, profileBefore);
            using var secondEntryHandle = OpenStateDestinationEntryNoFollow(parentHandle, requestedName);
            var secondNativeEntry = WindowsNative.QueryEntry(secondEntryHandle.DangerousGetHandle());
            var secondEntry = ToEntryInfo(secondNativeEntry);
            RequireSameVolume(parentBefore.Identity, secondEntry.Identity);
            RequireDirectoryRecordMatches(secondRecord, secondNativeEntry);

            if (firstRecord != secondRecord || firstEntry != secondEntry)
                throw Unknown("The requested destination entry, stored names, or no-follow identity changed during observation.");

            var parentNativeAfter = WindowsNative.QueryEntry(parentHandle);
            var parentAfter = ToEntryInfo(parentNativeAfter);
            var profileAfter = ObserveNameSemantics(parentHandle);
            RequireExpectedObservationParent(parentAfter, expectedParentIdentity, expectedNameSemantics, parentHandle);
            if (!SameFullIdentity(parentNativeBefore.Identity, parentNativeAfter.Identity) ||
                parentAfter.Kind != parentBefore.Kind ||
                profileAfter != profileBefore)
            {
                throw Unknown("The held destination parent identity or lookup profile changed during entry observation.");
            }

            return new WindowsStateDestinationEntryObservation(
                requestedName,
                firstRecord.FileName,
                firstRecord.ShortName,
                parentBefore.Identity,
                profileBefore,
                firstEntry);
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("observe an exact state-destination directory entry", exception);
        }
    }

    private WindowsNative.StateDestinationDirectoryEntry QueryStateDestinationEntryOnFreshParent(
        IntPtr heldParent,
        WindowsNative.WindowsNativeEntry expectedParentNative,
        PhysicalStoreEntryInfo expectedParent,
        PhysicalStoreNameSemantics expectedSemantics,
        string requestedName)
    {
        using var queryParent = WindowsNative.OpenDirectoryById(heldParent, expectedParentNative.Identity);
        var queryParentNativeBefore = WindowsNative.QueryEntry(queryParent.DangerousGetHandle());
        var queryParentBefore = ToEntryInfo(queryParentNativeBefore);
        RequireExpectedObservationParent(queryParentBefore, expectedParent.Identity, expectedSemantics, queryParent.DangerousGetHandle());
        if (!SameFullIdentity(expectedParentNative.Identity, queryParentNativeBefore.Identity))
            throw Unknown("The independent directory-query handle does not identify the full held parent identity.");

        var queryProfileBefore = ObserveNameSemantics(queryParent.DangerousGetHandle());
        if (queryProfileBefore != expectedSemantics)
            throw Unknown("The independent directory-query handle has a different native lookup profile.");

        var result = WindowsNative.QueryStateDestinationDirectoryEntry(queryParent, requestedName);

        var queryParentNativeAfter = WindowsNative.QueryEntry(queryParent.DangerousGetHandle());
        var queryParentAfter = ToEntryInfo(queryParentNativeAfter);
        var queryProfileAfter = ObserveNameSemantics(queryParent.DangerousGetHandle());
        RequireExpectedObservationParent(queryParentAfter, expectedParent.Identity, expectedSemantics, queryParent.DangerousGetHandle());
        if (!SameFullIdentity(expectedParentNative.Identity, queryParentNativeAfter.Identity) ||
            queryProfileAfter != queryProfileBefore)
        {
            throw Unknown("The independent directory-query handle identity or lookup profile changed during the exact query.");
        }

        return result;
    }

    private SafeFileHandle OpenStateDestinationEntryNoFollow(IntPtr parent, string requestedName)
        => WindowsNative.OpenRelative(
            parent,
            requestedName,
            WindowsNative.FileReadAttributes | WindowsNative.Synchronize,
            WindowsNative.FileOpen,
            createOptions: 0);

    private void RequireExpectedObservationParent(
        PhysicalStoreEntryInfo parent,
        PhysicalFileIdentity expectedIdentity,
        PhysicalStoreNameSemantics expectedSemantics,
        IntPtr parentHandle)
    {
        if (parent.Kind != PhysicalStoreEntryKind.Directory || parent.Identity != expectedIdentity)
            throw Unknown("The state-destination observation parent is not the exact held directory.");
        if (ObserveNameSemantics(parentHandle) != expectedSemantics)
            throw Unknown("The state-destination observation parent has a different native lookup profile.");
    }

    private static void RequireDirectoryRecordMatches(
        WindowsNative.StateDestinationDirectoryEntry record,
        WindowsNative.WindowsNativeEntry opened)
    {
        var attributes = opened.Attributes.FileAttributes;
        var isReparsePoint = (attributes & WindowsNative.FileAttributeReparsePoint) != 0;
        var openedReparseTag = isReparsePoint ? opened.Attributes.ReparseTag : 0u;
        if (record.FileIdLow != opened.Identity.FileIdLow ||
            record.FileIdHigh != opened.Identity.FileIdHigh ||
            record.FileAttributes != attributes ||
            record.EndOfFile != opened.Standard.EndOfFile ||
            record.ReparsePointTag != openedReparseTag ||
            ((record.FileAttributes & WindowsNative.FileAttributeDirectory) != 0) != (opened.Standard.Directory != 0))
        {
            throw Unknown("The filtered directory record does not identify the exact requested no-follow entry.");
        }
    }

    internal static void RequireSelectedDirectoryRecord(
        WindowsNative.StateDestinationDirectoryEntry record,
        string requestedName,
        PhysicalStoreNameSemantics semantics)
    {
        if (semantics.CaseSensitive &&
            !string.Equals(record.FileName, requestedName, StringComparison.Ordinal) &&
            (record.ShortName.Length == 0 || !string.Equals(record.ShortName, requestedName, StringComparison.Ordinal)))
        {
            throw Unknown("The case-sensitive native query did not return the exact requested long or short directory name.");
        }
    }

    private static bool SameFullIdentity(WindowsNative.FileIdInfo left, WindowsNative.FileIdInfo right)
        => left.VolumeSerialNumber == right.VolumeSerialNumber &&
           left.FileIdLow == right.FileIdLow &&
           left.FileIdHigh == right.FileIdHigh;
}
