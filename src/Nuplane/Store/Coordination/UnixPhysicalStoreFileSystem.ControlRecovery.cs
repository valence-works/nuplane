using System.Text;
using Microsoft.Win32.SafeHandles;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Unix;

namespace Nuplane.Store.Coordination;

internal sealed partial class UnixPhysicalStoreFileSystem : IPhysicalStoreControlRecoveryFileSystem
{
    private static readonly UTF8Encoding RecoveryNameUtf8 = new(false, true);

    /// <inheritdoc />
    public ValueTask<PhysicalStoreLockedControlFile?> TryOpenAndLockControlFileForRemovalAt(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedIdentity)
    {
        var platform = RequireSupportedPlatform();
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        ValidateName(singleName);

        var prepared = PhysicalStorePublicationChecks.PrepareRemoval(this, this, parent, singleName, expectedIdentity);
        var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        PhysicalStoreFileHandle? file = null;
        PhysicalStoreSafeHandleLease? fileLease = null;
        try
        {
            var parentFd = GetFileDescriptor(parentLease);
            RequireRecoveryParentAndProfile(platform, parentFd, prepared);
            RequireRecoveryEntry(platform, parentFd, singleName, expectedIdentity);

            var fd = InvokeNative(
                "open an exactly identified control file for locked removal",
                () => UnixNative.OpenFileAt(platform, parentFd, singleName, FileAccess.ReadWrite));
            file = OwnFile(fd, createdExclusive: false);
            fileLease = file.AcquireScopedSafeHandle(_providerToken);
            RequireRecoveryFileHandle(platform, parentFd, fd, singleName, prepared);

            var lockResult = InvokeNative("try nonblocking exclusive control-file removal lock", () => UnixNative.TryLock(platform, fd));
            if (lockResult.Status == UnixLockStatus.Busy)
            {
                fileLease.Dispose();
                fileLease = null;
                file.Dispose();
                file = null;
                parentLease.Dispose();
                return ValueTask.FromResult<PhysicalStoreLockedControlFile?>(null);
            }
            if (lockResult.Status == UnixLockStatus.Failure)
                throw NativeFailure("try nonblocking exclusive control-file removal lock", lockResult.Error);

            RequireRecoveryFileHandle(platform, parentFd, fd, singleName, prepared);
            var token = new PhysicalStoreLockedControlFile(
                _providerToken,
                file,
                fileLease,
                parentLease,
                prepared,
                raw => InvokeNative("release the control-file removal lock", () => UnixNative.Unlock(platform, checked((int)raw.ToInt64()))));
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
        var platform = RequireSupportedPlatform();
        var prepared = file.CanonicalName;
        file.WithRemovalAttempt(_providerToken, (parentHandle, fileHandle, markMutationAttempted) =>
        {
            var parentFd = checked((int)parentHandle.ToInt64());
            var fd = checked((int)fileHandle.ToInt64());
            RequireRecoveryFileHandle(platform, parentFd, fd, prepared.Basename, prepared);
            markMutationAttempted();
            InvokeNative(
                "remove the exactly identified locked control file",
                () => UnixNative.UnlinkFileAt(platform, parentFd, prepared.Basename));
            RequireRecoveryAbsent(platform, parentFd, prepared);
            return true;
        });

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public PhysicalStoreEntryInfo InspectLockedControlFile(PhysicalStoreLockedControlFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var platform = RequireSupportedPlatform();
        var prepared = file.CanonicalName;
        return file.WithInspection(_providerToken, (parentHandle, fileHandle) =>
        {
            var parentFd = checked((int)parentHandle.ToInt64());
            var fd = checked((int)fileHandle.ToInt64());
            RequireRecoveryFileHandle(platform, parentFd, fd, prepared.Basename, prepared);
            return ToEntryInfo(InvokeNative("reinspect the active locked control-file entry", () => UnixNative.StatHandle(platform, fd)));
        });
    }

    /// <inheritdoc />
    public PhysicalStoreEntryInfo MoveControlFileNoReplaceAt(
        PhysicalStoreDirectoryHandle parent,
        string sourceName,
        PhysicalFileIdentity expectedSourceIdentity,
        string destinationName)
        => PublishControlFileAt(parent, sourceName, expectedSourceIdentity, destinationName, expectedDestinationIdentity: null);

    private void RequireRecoveryFileHandle(
        UnixPlatform platform,
        int parentFd,
        int fileFd,
        string suppliedName,
        PhysicalStoreCanonicalName prepared)
    {
        var parent = ToEntryInfo(InvokeNative("inspect the held control-recovery parent", () => UnixNative.StatHandle(platform, parentFd)));
        if (parent.Kind != PhysicalStoreEntryKind.Directory || parent.Identity != prepared.ParentIdentity)
            throw Unknown("The held control-recovery parent identity changed.");

        var handle = ToEntryInfo(InvokeNative("inspect the locked control-file handle", () => UnixNative.StatHandle(platform, fileFd)));
        RequireControlFile(handle, "The locked removal handle is linked or not a regular file.");
        if (handle.Identity != prepared.FileIdentity)
            throw Unknown("The locked removal handle no longer matches its prepared identity.");

        RequireRecoveryEntry(platform, parentFd, suppliedName, prepared.FileIdentity);
        RequireRecoveryParentAndProfile(platform, parentFd, prepared);

        byte[] nameBytes = InvokeNative(
            "recheck the exact locked control-file basename",
            () => UnixNative.FindEntryName(platform, parentFd, fileFd, MaximumRecoveryNameBytes));
        string canonicalName;
        try
        {
            canonicalName = RecoveryNameUtf8.GetString(nameBytes);
            PhysicalStoreCanonicalName.ValidateBasename(canonicalName, prepared.Semantics);
        }
        catch (Exception exception) when (exception is DecoderFallbackException or ArgumentException)
        {
            throw Unknown("The locked control file has no valid exact canonical basename.", exception);
        }

        if (!string.Equals(canonicalName, prepared.Basename, StringComparison.Ordinal) ||
            !string.Equals(suppliedName, prepared.Basename, StringComparison.Ordinal))
        {
            throw Unknown("Removal requires the exact canonical native control-file basename.");
        }
    }

    private void RequireRecoveryEntry(
        UnixPlatform platform,
        int parentFd,
        string singleName,
        PhysicalFileIdentity expectedIdentity)
    {
        var result = InvokeNative("recheck the no-follow control-file entry", () => UnixNative.StatAt(platform, parentFd, singleName));
        if (result.Status != UnixStatResultStatus.Success)
        {
            if (result.Status == UnixStatResultStatus.Failure)
                throw NativeFailure("recheck the no-follow control-file entry", result.Error);
            throw Unknown("The expected control-file entry is absent.");
        }

        var entry = ToEntryInfo(result.Metadata);
        RequireControlFile(entry, "The no-follow control-file entry is linked or not regular.");
        if (entry.Identity != expectedIdentity)
            throw Unknown("The no-follow control-file entry has a different identity.");
    }

    private void RequireRecoveryParentAndProfile(
        UnixPlatform platform,
        int parentFd,
        PhysicalStoreCanonicalName prepared)
    {
        var parent = ToEntryInfo(InvokeNative("recheck the held control-recovery parent", () => UnixNative.StatHandle(platform, parentFd)));
        if (parent.Kind != PhysicalStoreEntryKind.Directory || parent.Identity != prepared.ParentIdentity)
            throw Unknown("The held control-recovery parent changed identity or kind.");

        var profile = InvokeNative("recheck the control-recovery name profile", () => UnixNative.GetNameProfile(platform, parentFd));
        if (!string.Equals(profile.ProfileId, prepared.Semantics.ProfileId, StringComparison.Ordinal) ||
            profile.CaseSensitive != prepared.Semantics.CaseSensitive ||
            profile.NormalizationInsensitive != prepared.Semantics.NormalizationInsensitive)
        {
            throw Unknown("The held control-recovery parent's native name profile changed.");
        }
    }

    private void RequireRecoveryAbsent(UnixPlatform platform, int parentFd, PhysicalStoreCanonicalName prepared)
    {
        RequireRecoveryParentAndProfile(platform, parentFd, prepared);
        var result = InvokeNative("positively verify locked control-file removal", () => UnixNative.StatAt(platform, parentFd, prepared.Basename));
        if (result.Status == UnixStatResultStatus.Failure)
            throw NativeFailure("positively verify locked control-file removal", result.Error);
        if (result.Status != UnixStatResultStatus.Absent)
            throw Unknown("The locked control-file entry remains present after removal.");
    }

    private const int MaximumRecoveryNameBytes = 4096;
}
