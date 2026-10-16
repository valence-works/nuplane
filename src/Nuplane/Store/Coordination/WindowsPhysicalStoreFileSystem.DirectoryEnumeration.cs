using Microsoft.Win32.SafeHandles;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Windows;

namespace Nuplane.Store.Coordination;

internal sealed partial class WindowsPhysicalStoreFileSystem : IPhysicalStoreDirectoryEnumerationFileSystem
{
    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateChildNamesNoFollow(
        PhysicalStoreDirectoryHandle parent,
        int maximumEntries)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (maximumEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        RequireSupportedPlatform();

        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var parentState = GetState(parent);
        var parentHandle = parentLease.DangerousHandle;
        WindowsNative.WindowsNativeEntry parentNativeBefore;
        try
        {
            parentNativeBefore = WindowsNative.QueryEntry(parentHandle);
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("inspect the held directory before enumerating child names", exception);
        }

        var parentBefore = ToEntryInfo(parentNativeBefore);
        RequireKind(parentBefore, PhysicalStoreEntryKind.Directory, "Child-name enumeration requires a held directory.");
        if (parentBefore.Identity != parentState.Identity)
            throw Unknown("The held directory identity changed before child-name enumeration.");

        var semanticsBefore = ObserveNameSemantics(parentHandle);
        SafeFileHandle? cursor = null;
        try
        {
            // OpenFileById uses the held directory as a volume hint and returns an independent
            // file object/cursor without resolving any path or sharing the parent's cursor.
            cursor = WindowsNative.OpenDirectoryById(parentHandle, parentNativeBefore.Identity);

            var cursorHandle = cursor.DangerousGetHandle();
            var cursorBefore = QueryEntry(cursorHandle, "inspect the independently opened enumeration cursor");
            RequireKind(cursorBefore, PhysicalStoreEntryKind.Directory, "The relative enumeration cursor is not a directory.");
            if (cursorBefore.Identity != parentBefore.Identity)
                throw Unknown("The independent enumeration cursor does not identify the held parent directory.");
            RequireSameVolume(parentBefore.Identity, cursorBefore.Identity);

            var cursorSemanticsBefore = ObserveNameSemantics(cursorHandle);
            if (cursorSemanticsBefore != semanticsBefore)
                throw Unknown("The independent enumeration cursor has a different native name profile.");

            var names = WindowsNative.EnumerateDirectoryNames(cursor, maximumEntries);

            var cursorAfter = QueryEntry(cursorHandle, "recheck the independent enumeration cursor");
            var cursorSemanticsAfter = ObserveNameSemantics(cursorHandle);
            var parentAfter = QueryEntry(parentHandle, "recheck the held directory after child-name enumeration");
            var semanticsAfter = ObserveNameSemantics(parentHandle);
            if (cursorAfter.Kind != PhysicalStoreEntryKind.Directory ||
                cursorAfter.Identity != parentBefore.Identity ||
                cursorSemanticsAfter != semanticsBefore ||
                parentAfter.Kind != PhysicalStoreEntryKind.Directory ||
                parentAfter.Identity != parentBefore.Identity ||
                semanticsAfter != semanticsBefore)
            {
                throw Unknown("The held directory identity, kind, or native name profile changed during child-name enumeration.");
            }

            return names;
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("enumerate child names relative to a held directory", exception);
        }
        finally
        {
            cursor?.Dispose();
        }
    }
}
