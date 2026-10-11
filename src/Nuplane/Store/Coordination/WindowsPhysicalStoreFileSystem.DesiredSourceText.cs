using Microsoft.Win32.SafeHandles;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Windows;

namespace Nuplane.Store.Coordination;

internal sealed partial class WindowsPhysicalStoreFileSystem : IPhysicalStoreDesiredSourceTextReadFileSystem
{
    private const int DesiredSourceTextReadChunkBytes = 64 * 1024;
    private const int StatusAccessDenied = unchecked((int)0xC0000022);

    public async ValueTask<PhysicalStoreDesiredSourceTextReadResult> ReadDesiredSourceTextAsync(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalStoreEntryInfo expectedParent,
        PhysicalStoreNameSemantics expectedNameSemantics,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? afterNativeReadAsync)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(expectedParent);
        ArgumentNullException.ThrowIfNull(expectedNameSemantics);
        ValidateName(singleName);
        cancellationToken.ThrowIfCancellationRequested();
        RequireSupportedPlatform();

        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var parentHandle = parentLease.DangerousHandle;
        var parentBefore = QueryPackageEntry(parentHandle, "inspect desired-source parent before opening its file");
        RequireDesiredTextParent(parentBefore, expectedParent);
        var profileBefore = ObserveNameSemantics(parentHandle);
        RequireDesiredTextProfile(profileBefore, expectedNameSemantics);

        SafeFileHandle? opened = null;
        try
        {
            // This relative no-follow data open is the first operation on the configured leaf.
            opened = WindowsNative.OpenRelative(
                parentHandle,
                singleName,
                WindowsNative.FileReadData | WindowsNative.FileReadAttributes | WindowsNative.Synchronize,
                WindowsNative.FileOpen,
                WindowsNative.FileNonDirectoryFile,
                shareAccess: WindowsNative.ShareRead);
        }
        catch (WindowsNativeCallException exception)
            when (exception.NtStatus is { } status && WindowsNative.IsPositiveAbsence(status))
        {
            return VerifyDesiredTextMissing(parentHandle, singleName, parentBefore, profileBefore);
        }
        catch (WindowsNativeCallException exception) when (exception.NtStatus == WindowsNative.StatusFileIsADirectory)
        {
            return VerifyDesiredTextDirectory(parentHandle, singleName, parentBefore, profileBefore);
        }
        catch (WindowsNativeCallException exception)
            when (exception.NtStatus is WindowsNative.StatusSharingViolation or StatusAccessDenied)
        {
            return VerifyDesiredTextUnreadable(parentHandle, singleName, parentBefore, profileBefore);
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("open desired-source text relative to its held parent", exception);
        }

        try
        {
            var openedHandle = opened ?? throw Unknown("The native desired-source open returned no handle.");
            var fileHandle = openedHandle.DangerousGetHandle();
            var openedInfo = QueryPackageEntry(fileHandle, "inspect opened desired-source text");
            RequireExpectedPackageFile(openedInfo);
            RequireSameVolume(parentBefore.Identity, openedInfo.Identity);
            var canonical = ObservePackageCanonicalBasename(fileHandle, profileBefore);
            RequireExactPackageBasename(singleName, canonical);
            var file = OwnFile(openedHandle, createdExclusive: false, openedInfo.Identity);
            opened = null;
            using (file)
            using (var fileLease = file.AcquireScopedSafeHandle(_providerToken))
            {
                fileHandle = fileLease.DangerousHandle;
                void Validate(long requiredLength)
                    => ValidatePackageFileBinding(
                        parentHandle,
                        fileHandle,
                        singleName,
                        parentBefore,
                        openedInfo.Identity,
                        requiredLength,
                        profileBefore,
                        canonical);

                Validate(openedInfo.Length);
                if (openedInfo.Length > Array.MaxLength)
                    throw Refusal(PackageStoreAdmissionReason.UnsupportedFilesystem,
                        "The desired-source text exceeds the runtime's maximum representable byte-array length.");

                var content = new byte[checked((int)openedInfo.Length)];
                var offset = 0;
                while (offset < content.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Validate(openedInfo.Length);
                    var requested = Math.Min(DesiredSourceTextReadChunkBytes, content.Length - offset);
                    int read;
                    try
                    {
                        read = await Task.Run(
                                () => WindowsNative.ReadPackageFileAt(fileHandle, offset, content, offset, requested),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (WindowsNativeCallException exception)
                        when (exception.NtStatus is WindowsNative.StatusSharingViolation or StatusAccessDenied)
                    {
                        Validate(openedInfo.Length);
                        return PhysicalStoreDesiredSourceTextReadResult.Unreadable;
                    }
                    catch (WindowsNativeCallException exception)
                    {
                        throw NativeFailure("read bounded desired-source text", exception);
                    }

                    if (read <= 0 || read > requested)
                        throw Unknown("The desired-source file ended before its verified native length.");
                    offset += read;
                    Validate(openedInfo.Length);
                }

                if (afterNativeReadAsync is not null)
                    await afterNativeReadAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                Validate(openedInfo.Length);
                var finalInfo = QueryPackageEntry(fileHandle, "recheck desired-source file after its awaited native read");
                RequireExpectedPackageFile(finalInfo);
                if (finalInfo.Identity != openedInfo.Identity || finalInfo.Length != openedInfo.Length)
                    throw Unknown("The desired-source file identity or length changed during its awaited read.");
                return PhysicalStoreDesiredSourceTextReadResult.Readable(content);
            }
        }
        finally
        {
            opened?.Dispose();
        }
    }

    private PhysicalStoreDesiredSourceTextReadResult VerifyDesiredTextMissing(
        IntPtr parentHandle,
        string singleName,
        PhysicalStoreEntryInfo expectedParent,
        PhysicalStoreNameSemantics expectedProfile)
    {
        using var first = OpenChildForInspection(parentHandle, singleName);
        if (first is not null)
            throw Unknown("The desired-source file appeared while its absence was being classified.");
        var parentAfter = QueryPackageEntry(parentHandle, "recheck desired-source parent after file absence");
        var profileAfter = ObserveNameSemantics(parentHandle);
        using var second = OpenChildForInspection(parentHandle, singleName);
        if (second is not null || parentAfter.Kind != PhysicalStoreEntryKind.Directory ||
            parentAfter.Identity != expectedParent.Identity || profileAfter != expectedProfile)
        {
            throw Unknown("The desired-source file or parent changed during absence replay.");
        }

        return PhysicalStoreDesiredSourceTextReadResult.Missing;
    }

    private PhysicalStoreDesiredSourceTextReadResult VerifyDesiredTextDirectory(
        IntPtr parentHandle,
        string singleName,
        PhysicalStoreEntryInfo expectedParent,
        PhysicalStoreNameSemantics expectedProfile)
    {
        using var named = OpenChildForInspection(parentHandle, singleName)
            ?? throw Unknown("The desired-source directory disappeared during its classification.");
        var entry = QueryPackageEntry(named.DangerousGetHandle(), "classify desired-source directory");
        var parentAfter = QueryPackageEntry(parentHandle, "recheck desired-source parent after directory classification");
        var profileAfter = ObserveNameSemantics(parentHandle);
        using var replay = OpenChildForInspection(parentHandle, singleName)
            ?? throw Unknown("The desired-source directory disappeared during its final replay.");
        var replayed = QueryPackageEntry(replay.DangerousGetHandle(), "replay desired-source directory classification");
        if (entry.Kind != PhysicalStoreEntryKind.Directory || entry.ReparseTag is not null ||
            replayed.Kind != PhysicalStoreEntryKind.Directory || replayed.ReparseTag is not null ||
            replayed.Identity != entry.Identity || parentAfter.Kind != PhysicalStoreEntryKind.Directory ||
            parentAfter.Identity != expectedParent.Identity || profileAfter != expectedProfile)
        {
            throw Unknown("The desired-source entry is not the same ordinary directory under its exact parent profile.");
        }

        return PhysicalStoreDesiredSourceTextReadResult.Directory;
    }

    private PhysicalStoreDesiredSourceTextReadResult VerifyDesiredTextUnreadable(
        IntPtr parentHandle,
        string singleName,
        PhysicalStoreEntryInfo expectedParent,
        PhysicalStoreNameSemantics expectedProfile)
    {
        SafeFileHandle? metadata = null;
        try
        {
            // Metadata-only access can positively classify a file blocked by the legacy ReadData/ShareRead open.
            metadata = WindowsNative.OpenRelative(
                parentHandle,
                singleName,
                WindowsNative.FileReadAttributes | WindowsNative.Synchronize,
                WindowsNative.FileOpen,
                WindowsNative.FileNonDirectoryFile,
                shareAccess: WindowsNative.ShareRead | WindowsNative.ShareWrite);
            var info = QueryPackageEntry(metadata.DangerousGetHandle(), "classify unreadable desired-source text");
            RequireExpectedPackageFile(info);
            RequireSameVolume(expectedParent.Identity, info.Identity);
            var canonical = ObservePackageCanonicalBasename(metadata.DangerousGetHandle(), expectedProfile);
            RequireExactPackageBasename(singleName, canonical);
            using var named = OpenChildForInspection(parentHandle, singleName)
                ?? throw Unknown("The unreadable desired-source file disappeared during metadata replay.");
            var namedInfo = QueryPackageEntry(named.DangerousGetHandle(), "replay unreadable desired-source entry");
            var after = QueryPackageEntry(metadata.DangerousGetHandle(), "recheck unreadable desired-source file");
            var parentAfter = QueryPackageEntry(parentHandle, "recheck desired-source parent after unreadable classification");
            var profileAfter = ObserveNameSemantics(parentHandle);
            if (after.Kind != PhysicalStoreEntryKind.RegularFile || after.LinkCount != 1 ||
                namedInfo.Kind != PhysicalStoreEntryKind.RegularFile || namedInfo.LinkCount != 1 ||
                after.Identity != info.Identity || namedInfo.Identity != info.Identity || after.Length != info.Length ||
                namedInfo.Length != info.Length || parentAfter.Kind != PhysicalStoreEntryKind.Directory ||
                parentAfter.Identity != expectedParent.Identity || profileAfter != expectedProfile)
            {
                throw Unknown("The unreadable desired-source file or its parent changed during metadata replay.");
            }

            return PhysicalStoreDesiredSourceTextReadResult.Unreadable;
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("positively classify unreadable desired-source text", exception);
        }
        finally
        {
            metadata?.Dispose();
        }
    }

    private static void RequireDesiredTextParent(PhysicalStoreEntryInfo actual, PhysicalStoreEntryInfo expected)
    {
        if (actual.Kind != PhysicalStoreEntryKind.Directory || expected.Kind != PhysicalStoreEntryKind.Directory ||
            actual.Identity != expected.Identity)
        {
            throw Unknown("The desired-source parent identity changed before its text read.");
        }
    }

    private static void RequireDesiredTextProfile(
        PhysicalStoreNameSemantics actual,
        PhysicalStoreNameSemantics expected)
    {
        if (actual != expected)
            throw Unknown("The desired-source parent name profile changed before its text read.");
    }
}
