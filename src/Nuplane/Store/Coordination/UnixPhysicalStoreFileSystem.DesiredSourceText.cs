using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Unix;

namespace Nuplane.Store.Coordination;

internal sealed partial class UnixPhysicalStoreFileSystem : IPhysicalStoreDesiredSourceTextReadFileSystem
{
    private const int DesiredSourceTextReadChunkBytes = 64 * 1024;

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

        var platform = RequireSupportedPlatform();
        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var parentFd = GetFileDescriptor(parentLease);
        var parentBefore = ToEntryInfo(InvokeNative("inspect desired-source parent before opening its file", () => UnixNative.StatHandle(platform, parentFd)));
        RequireDesiredTextParent(parentBefore, expectedParent);
        var profileBefore = ObservePackageNameProfile(platform, parentFd);
        RequireDesiredTextProfile(profileBefore, expectedNameSemantics);

        int fileDescriptor;
        try
        {
            // This parent-relative O_NOFOLLOW open is the first operation on the configured leaf.
            fileDescriptor = UnixNative.OpenFileAt(platform, parentFd, singleName, FileAccess.Read);
        }
        catch (UnixNativeCallException exception) when (exception.Error == 2)
        {
            var absent = InspectChildNoFollow(parent, singleName);
            var parentAfter = ToEntryInfo(InvokeNative("recheck desired-source parent after file absence", () => UnixNative.StatHandle(platform, parentFd)));
            var profileAfter = ObservePackageNameProfile(platform, parentFd);
            var absentAfter = InspectChildNoFollow(parent, singleName);
            if (absent is not null || absentAfter is not null || parentAfter.Identity != parentBefore.Identity ||
                parentAfter.Kind != PhysicalStoreEntryKind.Directory || profileAfter != profileBefore)
            {
                throw Unknown("The desired-source file changed while its absence was being classified.");
            }

            return PhysicalStoreDesiredSourceTextReadResult.Missing;
        }
        catch (UnixNativeCallException exception) when (exception.Error == 21)
        {
            return VerifyDesiredTextDirectory(parent, parentFd, singleName, parentBefore, profileBefore);
        }
        catch (UnixNativeCallException exception)
        {
            // A denied open without a held file identity cannot establish the exact canonical leaf.
            // Preserve the typed refusal instead of projecting uncertain authority into Unreadable.
            throw NativeFailure("open desired-source text relative to its held parent", exception.Error, exception);
        }

        using var file = OwnFile(fileDescriptor, createdExclusive: false);
        using var fileLease = file.AcquireScopedSafeHandle(_providerToken);
        var fd = GetFileDescriptor(fileLease);
        var opened = ToEntryInfo(InvokeNative("inspect the opened desired-source file", () => UnixNative.StatHandle(platform, fd)));
        if (opened.Kind == PhysicalStoreEntryKind.Directory)
        {
            var namedDirectory = InspectChildNoFollow(parent, singleName);
            var parentAfterDirectory = ToEntryInfo(InvokeNative("recheck desired-source parent after directory classification", () => UnixNative.StatHandle(platform, parentFd)));
            var profileAfterDirectory = ObservePackageNameProfile(platform, parentFd);
            var namedDirectoryAfter = InspectChildNoFollow(parent, singleName);
            if (namedDirectory is not { Kind: PhysicalStoreEntryKind.Directory } || namedDirectory.Identity != opened.Identity ||
                namedDirectoryAfter is not { Kind: PhysicalStoreEntryKind.Directory } || namedDirectoryAfter.Identity != opened.Identity ||
                parentAfterDirectory.Identity != parentBefore.Identity || parentAfterDirectory.Kind != PhysicalStoreEntryKind.Directory ||
                profileAfterDirectory != profileBefore)
            {
                throw Unknown("The desired-source directory changed during no-follow classification.");
            }

            return PhysicalStoreDesiredSourceTextReadResult.Directory;
        }

        RequireExpectedPackageFile(opened);
        var canonical = ObservePackageCanonicalBasename(platform, parentFd, fd);
        RequireExactPackageBasename(singleName, canonical);

        var lockResult = InvokeNative("try a nonblocking shared desired-source read lock",
            () => UnixNative.TrySharedReadLock(platform, fd));
        if (lockResult.Status == UnixLockStatus.Failure)
            throw NativeFailure("try a nonblocking shared desired-source read lock", lockResult.Error);
        if (lockResult.Status == UnixLockStatus.Busy)
        {
            ValidatePackageFileBinding(platform, parentFd, fd, singleName, parentBefore,
                opened.Identity, opened.Length, profileBefore, canonical);
            return PhysicalStoreDesiredSourceTextReadResult.Unreadable;
        }

        try
        {
            var afterLock = ToEntryInfo(InvokeNative("observe desired-source file after acquiring its shared lock", () => UnixNative.StatHandle(platform, fd)));
            RequireExpectedPackageFile(afterLock);
            if (afterLock.Identity != opened.Identity)
                throw Unknown("The desired-source file identity changed while acquiring its shared read lock.");

            var length = afterLock.Length;
            ValidatePackageFileBinding(platform, parentFd, fd, singleName, parentBefore,
                afterLock.Identity, length, profileBefore, canonical);
            if (length > Array.MaxLength)
                throw Refusal(PackageStoreAdmissionReason.UnsupportedFilesystem,
                    "The desired-source text exceeds the runtime's maximum representable byte-array length.");

            var content = new byte[checked((int)length)];
            var offset = 0;
            while (offset < content.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidatePackageFileBinding(platform, parentFd, fd, singleName, parentBefore,
                    afterLock.Identity, length, profileBefore, canonical);
                var requested = Math.Min(DesiredSourceTextReadChunkBytes, content.Length - offset);
                int read;
                try
                {
                    read = await Task.Run(
                            () => UnixNative.ReadPackageFileAt(platform, fd, content, offset, requested, offset),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (UnixNativeCallException exception) when (IsDesiredSourceReadFailure(exception.Error))
                {
                    ValidatePackageFileBinding(platform, parentFd, fd, singleName, parentBefore,
                        afterLock.Identity, length, profileBefore, canonical);
                    return PhysicalStoreDesiredSourceTextReadResult.Unreadable;
                }
                catch (UnixNativeCallException exception)
                {
                    throw NativeFailure("read bounded desired-source text", exception.Error, exception);
                }
                if (read <= 0 || read > requested)
                    throw Unknown("The desired-source file ended before its verified native length.");
                offset += read;
                ValidatePackageFileBinding(platform, parentFd, fd, singleName, parentBefore,
                    afterLock.Identity, length, profileBefore, canonical);
            }

            if (afterNativeReadAsync is not null)
                await afterNativeReadAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePackageFileBinding(platform, parentFd, fd, singleName, parentBefore,
                afterLock.Identity, length, profileBefore, canonical);
            var finalInfo = ToEntryInfo(InvokeNative(
                "recheck desired-source file after its awaited native read",
                () => UnixNative.StatHandle(platform, fd)));
            RequireExpectedPackageFile(finalInfo);
            if (finalInfo.Identity != afterLock.Identity || finalInfo.Length != length)
                throw Unknown("The desired-source file identity or length changed during its awaited read.");
            return PhysicalStoreDesiredSourceTextReadResult.Readable(content);
        }
        finally
        {
            InvokeNative("release the shared desired-source read lock", () => UnixNative.Unlock(platform, fd));
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

    private PhysicalStoreDesiredSourceTextReadResult VerifyDesiredTextDirectory(
        PhysicalStoreDirectoryHandle parent,
        int parentFd,
        string singleName,
        PhysicalStoreEntryInfo expectedParent,
        UnixNameProfile expectedProfile)
    {
        var first = InspectChildNoFollow(parent, singleName);
        var parentAfter = ToEntryInfo(InvokeNative(
            "recheck desired-source parent after directory classification",
            () => UnixNative.StatHandle(RequireSupportedPlatform(), parentFd)));
        var profileAfter = ObservePackageNameProfile(RequireSupportedPlatform(), parentFd);
        var second = InspectChildNoFollow(parent, singleName);
        if (first is not { Kind: PhysicalStoreEntryKind.Directory } ||
            second is not { Kind: PhysicalStoreEntryKind.Directory } || first.Identity != second.Identity ||
            parentAfter.Kind != PhysicalStoreEntryKind.Directory || parentAfter.Identity != expectedParent.Identity ||
            profileAfter != expectedProfile)
        {
            throw Unknown("The desired-source entry is not the same ordinary directory under its exact parent profile.");
        }

        return PhysicalStoreDesiredSourceTextReadResult.Directory;
    }

    private static void RequireDesiredTextProfile(UnixNameProfile actual, PhysicalStoreNameSemantics expected)
    {
        if (!string.Equals(actual.ProfileId, expected.ProfileId, StringComparison.Ordinal) ||
            actual.CaseSensitive != expected.CaseSensitive ||
            actual.NormalizationInsensitive != expected.NormalizationInsensitive ||
            expected.Encoding != PhysicalStoreNameEncoding.Utf8)
        {
            throw Unknown("The desired-source parent name profile changed before its text read.");
        }
    }

    private static bool IsDesiredSourceReadFailure(int error)
        // EIO, EACCES, EPERM, EBUSY and EAGAIN are positively classified read/lock failures.
        => error is 5 or 13 or 1 or 16 or 11;
}
