using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Windows;

namespace Nuplane.Store.Coordination;

internal sealed partial class WindowsPhysicalStoreFileSystem : IPhysicalStoreControlRecoveryFileSystem
{
    /// <inheritdoc />
    public ValueTask<PhysicalStoreLockedControlFile?> TryOpenAndLockControlFileForRemovalAt(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedIdentity)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        ValidateName(singleName);
        RequireSupportedPlatform();

        var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        PhysicalStoreFileHandle? file = null;
        PhysicalStoreSafeHandleLease? fileLease = null;
        try
        {
            var parentRaw = parentLease.DangerousHandle;
            // The ordinary canonical-name observer reopens with a handle that does not share
            // DELETE. A compatible incumbent recovery token already has DELETE access, so use
            // recovery-local replay with publication-style share-delete inspection handles.
            var prepared = PrepareRecoveryRemovalAt(parent, parentRaw, singleName, expectedIdentity);
            RequireRecoveryParentAndProfile(parentRaw, prepared);
            RecheckPublicationEntry(parentRaw, singleName, expectedIdentity);

            SafeFileHandle opened;
            try
            {
                opened = WindowsNative.OpenRelative(
                    parentRaw,
                    singleName,
                    WindowsNative.DeleteAccess |
                    WindowsNative.FileReadData |
                    WindowsNative.FileWriteData |
                    WindowsNative.FileReadAttributes |
                    WindowsNative.Synchronize,
                    WindowsNative.FileOpen,
                    WindowsNative.FileNonDirectoryFile,
                    shareAccess: WindowsNative.ShareRead | WindowsNative.ShareWrite | WindowsNative.ShareDelete);
            }
            catch (WindowsNativeCallException exception)
            {
                throw NativeFailure("open an exactly identified control file for locked removal", exception);
            }

            try
            {
                var openedInfo = QueryEntry(opened.DangerousGetHandle(), "inspect the control-file removal handle");
                RequireControlFile(openedInfo, "The removal handle is a link or not a regular single-link file.");
                if (openedInfo.Identity != expectedIdentity)
                    throw Unknown("The opened control-file removal handle has a different identity.");
                file = OwnFile(opened, createdExclusive: false, openedInfo.Identity);
                opened = null!;
            }
            finally
            {
                opened?.Dispose();
            }

            fileLease = file.AcquireScopedSafeHandle(_providerToken);
            var fileRaw = fileLease.DangerousHandle;
            RequireRecoveryFileHandle(parentRaw, fileRaw, singleName, prepared);

            var overlapped = default(WindowsNative.Overlapped);
            try
            {
                if (!WindowsNative.TryLock(fileRaw, ref overlapped))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == WindowsNative.ErrorLockViolation)
                    {
                        fileLease.Dispose();
                        fileLease = null;
                        file.Dispose();
                        file = null;
                        parentLease.Dispose();
                        return ValueTask.FromResult<PhysicalStoreLockedControlFile?>(null);
                    }

                    throw NativeError("try nonblocking exclusive control-file removal lock", error);
                }
            }
            catch (WindowsNativeCallException exception)
            {
                throw NativeFailure("try nonblocking exclusive control-file removal lock", exception);
            }

            RequireRecoveryFileHandle(parentRaw, fileRaw, singleName, prepared);
            var token = new PhysicalStoreLockedControlFile(
                _providerToken,
                file,
                fileLease,
                parentLease,
                prepared,
                raw =>
                {
                    var nativeOverlapped = overlapped;
                    try
                    {
                        WindowsNative.Unlock(raw, ref nativeOverlapped);
                    }
                    catch (WindowsNativeCallException exception)
                    {
                        throw NativeFailure("release the control-file removal lock", exception);
                    }
                });
            file = null;
            fileLease = null;
            parentLease = null!;
            return ValueTask.FromResult<PhysicalStoreLockedControlFile?>(token);
        }
        catch
        {
            fileLease?.Dispose();
            file?.Dispose();
            parentLease?.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask RemoveLockedControlFileAsync(PhysicalStoreLockedControlFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        RequireSupportedPlatform();
        var prepared = file.CanonicalName;
        file.WithRemovalAttempt(_providerToken, (parentRaw, fileRaw, markMutationAttempted) =>
        {
            RequireRecoveryFileHandle(parentRaw, fileRaw, prepared.Basename, prepared);
            markMutationAttempted();
            try
            {
                // FILE_DISPOSITION_INFORMATION may leave deletion pending. After success or an
                // uncertain native error, token cleanup may only close this same handle directly.
                WindowsNative.RemoveControlFile(fileRaw);
            }
            catch (WindowsNativeCallException exception)
            {
                throw NativeFailure("remove the exactly identified locked control file", exception);
            }

            return true;
        }, parentRaw => VerifyRecoveryAbsent(parentRaw, prepared));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public PhysicalStoreEntryInfo MoveControlFileNoReplaceAt(
        PhysicalStoreDirectoryHandle parent,
        string sourceName,
        PhysicalFileIdentity expectedSourceIdentity,
        string destinationName)
        => PublishControlFileAt(parent, sourceName, expectedSourceIdentity, destinationName, expectedDestinationIdentity: null);

    private void RequireRecoveryFileHandle(
        IntPtr parentRaw,
        IntPtr fileRaw,
        string suppliedName,
        PhysicalStoreCanonicalName prepared)
    {
        var parent = QueryEntry(parentRaw, "inspect the held control-recovery parent");
        if (parent.Kind != PhysicalStoreEntryKind.Directory || parent.Identity != prepared.ParentIdentity)
            throw Unknown("The held control-recovery parent identity changed.");

        var handle = QueryEntry(fileRaw, "inspect the locked control-file handle");
        RequireControlFile(handle, "The locked removal handle is linked or not a regular file.");
        if (handle.Identity != prepared.FileIdentity)
            throw Unknown("The locked removal handle no longer matches its prepared identity.");

        RecheckPublicationEntry(parentRaw, suppliedName, prepared.FileIdentity);
        RequireRecoveryParentAndProfile(parentRaw, prepared);

        var canonicalName = GetRecoveryCanonicalBasename(fileRaw, prepared.Semantics);

        if (!string.Equals(canonicalName, prepared.Basename, StringComparison.Ordinal) ||
            !string.Equals(suppliedName, prepared.Basename, StringComparison.Ordinal))
        {
            throw Unknown("Removal requires the exact canonical native control-file basename.");
        }
    }

    private PhysicalStoreCanonicalName PrepareRecoveryRemovalAt(
        PhysicalStoreDirectoryHandle parent,
        IntPtr parentRaw,
        string singleName,
        PhysicalFileIdentity expectedIdentity)
    {
        var parentState = GetState(parent);
        var parentBefore = QueryEntry(parentRaw, "inspect the held parent before preparing control-file removal");
        if (parentBefore.Kind != PhysicalStoreEntryKind.Directory || parentBefore.Identity != parentState.Identity)
            throw Unknown("The held control-recovery parent changed identity or kind before preparation.");

        var semanticsBefore = ObserveNameSemantics(parentRaw);
        using var original = OpenRecoveryInspectionFile(parentRaw, singleName, "open the supplied control-file name");
        var originalInfo = QueryEntry(original.DangerousGetHandle(), "inspect the supplied control-file name");
        RequireControlFile(originalInfo, "Removal requires an exact regular single-link control file.");
        if (originalInfo.Identity != expectedIdentity)
            throw Unknown("The supplied control-file name does not resolve to the expected file identity.");
        RequireSameVolume(parentBefore.Identity, originalInfo.Identity);

        var basename = GetRecoveryCanonicalBasename(original.DangerousGetHandle(), semanticsBefore);
        using var canonical = OpenRecoveryInspectionFile(parentRaw, basename, "reopen the canonical control-file name");
        var canonicalInfo = QueryEntry(canonical.DangerousGetHandle(), "inspect the canonical control-file name");
        RequireControlFile(canonicalInfo, "The canonical control-file name is linked or not regular.");
        if (canonicalInfo.Identity != expectedIdentity)
            throw Unknown("The canonical control-file name has a different identity.");
        RequireSameVolume(parentBefore.Identity, canonicalInfo.Identity);

        // Replay the caller's exact spelling as well as the normalized spelling while all
        // inspection handles permit the incumbent DELETE-capable handle to remain open.
        using var supplied = OpenRecoveryInspectionFile(parentRaw, singleName, "replay the supplied control-file name");
        var suppliedInfo = QueryEntry(supplied.DangerousGetHandle(), "reinspect the supplied control-file name");
        RequireControlFile(suppliedInfo, "The supplied control-file name is linked or not regular.");
        if (suppliedInfo.Identity != expectedIdentity || originalInfo.Identity != expectedIdentity)
            throw Unknown("The supplied control-file name changed identity during preparation.");
        RequireSameVolume(parentBefore.Identity, suppliedInfo.Identity);

        var parentAfter = QueryEntry(parentRaw, "recheck the held parent after preparing control-file removal");
        var semanticsAfter = ObserveNameSemantics(parentRaw);
        if (parentAfter.Kind != PhysicalStoreEntryKind.Directory ||
            parentAfter.Identity != parentBefore.Identity ||
            semanticsAfter != semanticsBefore)
        {
            throw Unknown("The parent or native name profile changed during control-file removal preparation.");
        }

        if (!string.Equals(basename, singleName, StringComparison.Ordinal))
            throw Unknown("Removal requires the exact canonical native control-file basename.");

        return new PhysicalStoreCanonicalName(parentBefore.Identity, expectedIdentity, basename, semanticsBefore);
    }

    private static SafeFileHandle OpenRecoveryInspectionFile(IntPtr parentRaw, string name, string operation)
    {
        try
        {
            return OpenPublicationFile(parentRaw, name, requestDelete: false);
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure(operation, exception);
        }
    }

    private static string GetRecoveryCanonicalBasename(IntPtr fileRaw, PhysicalStoreNameSemantics semantics)
    {
        string normalizedPath;
        try
        {
            normalizedPath = WindowsNative.GetNormalizedVolumePath(fileRaw);
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("recheck the exact locked control-file basename", exception);
        }

        var separator = normalizedPath.LastIndexOf('\\');
        if (separator < 0 || separator == normalizedPath.Length - 1)
            throw Unknown("The locked control file has no exact canonical basename.");
        var canonicalName = normalizedPath[(separator + 1)..];
        try
        {
            PhysicalStoreCanonicalName.ValidateBasename(canonicalName, semantics);
        }
        catch (ArgumentException exception)
        {
            throw Unknown("The locked control file has an invalid canonical basename.", exception);
        }

        return canonicalName;
    }

    private void RequireRecoveryParentAndProfile(IntPtr parentRaw, PhysicalStoreCanonicalName prepared)
    {
        var parent = QueryEntry(parentRaw, "recheck the held control-recovery parent");
        if (parent.Kind != PhysicalStoreEntryKind.Directory || parent.Identity != prepared.ParentIdentity)
            throw Unknown("The held control-recovery parent changed identity or kind.");
        if (ObserveNameSemantics(parentRaw) != prepared.Semantics)
            throw Unknown("The held control-recovery parent's native name profile changed.");
    }

    private void VerifyRecoveryAbsent(IntPtr parentRaw, PhysicalStoreCanonicalName prepared)
    {
        RequireRecoveryParentAndProfile(parentRaw, prepared);
        RecheckPublicationEntry(parentRaw, prepared.Basename, expectedIdentity: null);
    }
}
