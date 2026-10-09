using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Unix;

namespace Nuplane.Store.Coordination;

/// <summary>Performs bounded no-follow metadata, control-file, and prepared-directory operations on supported Unix systems.</summary>
/// <remarks>
/// The adapter intentionally supports only Darwin arm64 and Linux x64/arm64. It does not resolve arbitrary
/// paths or delete package content.
/// All child operations are relative to handles created by
/// this provider, and all native calls use a scoped SafeHandle reference. Publication companions provide
/// single-file state updates and same-parent no-replace moves of verified prepared directories.
/// </remarks>
internal sealed partial class UnixPhysicalStoreFileSystem : IPhysicalStoreFileSystem, IPhysicalStoreNameFileSystem, IPhysicalStoreDirectoryPublicationFileSystem
{
    private const int MaximumLinkTargetBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly object _providerToken = new();

    internal static bool IsSupportedPlatform
        => UnixNative.GetPlatform() is not null;

    /// <inheritdoc />
    public PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor)
    {
        var platform = RequireSupportedPlatform();
        if (!string.Equals(anchor, "/", StringComparison.Ordinal))
        {
            throw Refusal(
                PackageStoreAdmissionReason.UnsupportedFilesystem,
                "The Unix provider can open only the namespace root '/'.");
        }

        var fd = InvokeNative("open namespace root", () => UnixNative.OpenNamespaceRoot(platform));
        var handle = OwnDirectory(fd);
        try
        {
            var info = InspectHandle(handle);
            if (info.Kind != PhysicalStoreEntryKind.Directory)
                throw Unknown("The Unix namespace root is not a directory.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
    {
        var platform = RequireSupportedPlatform();
        ArgumentNullException.ThrowIfNull(parent);
        ValidateName(singleName);
        using var parentHandle = parent.AcquireScopedSafeHandle(_providerToken);
        var result = InvokeNative(
            "inspect child without following links",
            () => UnixNative.StatAt(platform, GetFileDescriptor(parentHandle), singleName));
        if (result.Status == UnixStatResultStatus.Absent)
            return null;
        if (result.Status == UnixStatResultStatus.Failure)
            throw NativeFailure("inspect child without following links", result.Error);
        return ToEntryInfo(result.Metadata);
    }

    /// <inheritdoc />
    public PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
    {
        var platform = RequireSupportedPlatform();
        ArgumentNullException.ThrowIfNull(parent);
        ValidateName(singleName);
        var observed = InspectChildNoFollow(parent, singleName);
        if (observed is null || observed.Kind != PhysicalStoreEntryKind.Directory)
            throw Unknown("The requested child is not a positively observed directory.");

        using var parentHandle = parent.AcquireScopedSafeHandle(_providerToken);
        var fd = InvokeNative(
            "open child directory without following links",
            () => UnixNative.OpenDirectoryAt(platform, GetFileDescriptor(parentHandle), singleName));
        var opened = OwnDirectory(fd);
        try
        {
            var actual = InspectHandle(opened);
            if (actual.Kind != PhysicalStoreEntryKind.Directory || actual.Identity != observed.Identity)
                throw Unknown("The directory changed between no-follow inspection and open.");

            return opened;
        }
        catch
        {
            opened.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory)
    {
        var platform = RequireSupportedPlatform();
        ArgumentNullException.ThrowIfNull(directory);
        using var directoryHandle = directory.AcquireScopedSafeHandle(_providerToken);
        var fd = InvokeNative(
            "open held directory parent",
            () => UnixNative.OpenDirectoryAt(platform, GetFileDescriptor(directoryHandle), ".."));
        var parent = OwnDirectory(fd);
        try
        {
            if (InspectHandle(parent).Kind != PhysicalStoreEntryKind.Directory)
                throw Unknown("The opened parent is not a directory.");
            return parent;
        }
        catch
        {
            parent.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public PhysicalStoreFileHandle OpenFileChildNoFollow(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        FileAccess access)
    {
        var platform = RequireSupportedPlatform();
        ArgumentNullException.ThrowIfNull(parent);
        ValidateName(singleName);
        var observed = InspectChildNoFollow(parent, singleName);
        RequireControlFile(observed, "The requested control file is absent, linked, or not a regular file.");

        using var parentHandle = parent.AcquireScopedSafeHandle(_providerToken);
        var fd = InvokeNative(
            "open child control file without following links",
            () => UnixNative.OpenFileAt(platform, GetFileDescriptor(parentHandle), singleName, access));
        var opened = OwnFile(fd, createdExclusive: false);
        try
        {
            var actual = InspectHandle(opened);
            RequireControlFile(actual, "The opened control file is linked or not a regular file.");
            if (actual.Identity != observed!.Identity)
                throw Unknown("The control file changed between no-follow inspection and open.");
            return opened;
        }
        catch
        {
            opened.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public string ReadLinkTargetNoFollow(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedLinkIdentity)
    {
        var platform = RequireSupportedPlatform();
        ArgumentNullException.ThrowIfNull(expectedLinkIdentity);
        ArgumentNullException.ThrowIfNull(parent);
        ValidateName(singleName);

        var before = InspectChildNoFollow(parent, singleName);
        if (before is null || before.Kind != PhysicalStoreEntryKind.SymbolicLink || before.Identity != expectedLinkIdentity)
            throw Unknown("The symbolic link does not match the expected no-follow identity.");

        byte[] targetBytes;
        using (var parentHandle = parent.AcquireScopedSafeHandle(_providerToken))
        {
            targetBytes = InvokeNative(
                "read symbolic-link target",
                () => UnixNative.ReadLinkAt(
                    platform,
                    GetFileDescriptor(parentHandle),
                    singleName,
                    MaximumLinkTargetBytes));
        }

        var after = InspectChildNoFollow(parent, singleName);
        if (after is null || after.Kind != PhysicalStoreEntryKind.SymbolicLink || after.Identity != expectedLinkIdentity)
            throw Unknown("The symbolic link changed while its target was read.");

        try
        {
            return StrictUtf8.GetString(targetBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw Unknown("The symbolic-link target is not valid UTF-8.", exception);
        }
    }

    /// <inheritdoc />
    public PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle)
    {
        var platform = RequireSupportedPlatform();
        ArgumentNullException.ThrowIfNull(handle);
        using var scopedHandle = handle.AcquireScopedSafeHandle(_providerToken);
        var metadata = InvokeNative(
            "inspect held handle",
            () => UnixNative.StatHandle(platform, GetFileDescriptor(scopedHandle)));
        return ToEntryInfo(metadata);
    }

    /// <inheritdoc />
    public PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
    {
        var platform = RequireSupportedPlatform();
        ValidateName(singleName);
        ArgumentNullException.ThrowIfNull(parent);
        var beforeCreate = InspectChildNoFollow(parent, singleName);
        if (beforeCreate is not null)
            throw Unknown("The directory name already exists.");

        using var parentHandle = parent.AcquireScopedSafeHandle(_providerToken);
        InvokeNative(
            "create child directory exclusively",
            () => UnixNative.CreateDirectoryAt(platform, GetFileDescriptor(parentHandle), singleName));

        // This is a post-create observation, not an atomic create/open guarantee against arbitrary
        // filesystem writers. The owned root lock is the cooperating-writer boundary.
        var namedBeforeOpen = InspectChildNoFollow(parent, singleName);
        if (namedBeforeOpen is null || namedBeforeOpen.Kind != PhysicalStoreEntryKind.Directory)
            throw Unknown("The newly created directory could not be observed without following links.");

        var fd = InvokeNative(
            "open exclusively created directory",
            () => UnixNative.OpenDirectoryAt(platform, GetFileDescriptor(parentHandle), singleName));
        var created = OwnDirectory(fd);
        try
        {
            var openedInfo = InspectHandle(created);
            var namedInfo = InspectChildNoFollow(parent, singleName);
            if (openedInfo.Kind != PhysicalStoreEntryKind.Directory ||
                namedInfo is null ||
                namedInfo.Kind != PhysicalStoreEntryKind.Directory ||
                openedInfo.Identity != namedBeforeOpen.Identity ||
                namedInfo.Identity != openedInfo.Identity)
            {
                throw Unknown("The created directory changed before its identity was verified.");
            }

            return created;
        }
        catch
        {
            created.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
    {
        var platform = RequireSupportedPlatform();
        ArgumentNullException.ThrowIfNull(parent);
        ValidateName(singleName);
        using var parentHandle = parent.AcquireScopedSafeHandle(_providerToken);
        var fd = InvokeNative(
            "create control file exclusively",
            () => UnixNative.CreateFileAt(platform, GetFileDescriptor(parentHandle), singleName));
        var created = OwnFile(fd, createdExclusive: true);
        try
        {
            var createdInfo = InspectHandle(created);
            RequireControlFile(createdInfo, "The exclusively created control file is not a single-link regular file.");
            if (createdInfo.Length != 0)
                throw Unknown("A newly created control file was not empty.");
            return created;
        }
        catch
        {
            created.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes)
    {
        var platform = RequireSupportedPlatform();
        ArgumentNullException.ThrowIfNull(file);
        if (maximumBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        var before = InspectHandle(file);
        RequireControlFile(before, "The control file is linked or not a regular file.");
        if (before.Length > maximumBytes || before.Length > int.MaxValue)
            throw Unknown("The control file exceeds the requested read bound.");

        var bytes = new byte[checked((int)before.Length)];
        using (var scopedHandle = file.AcquireScopedSafeHandle(_providerToken))
        {
            var fd = GetFileDescriptor(scopedHandle);
            var current = ToEntryInfo(InvokeNative("recheck bounded control file before reading", () => UnixNative.StatHandle(platform, fd)));
            RequireControlFile(current, "The control file changed before its bounded contents were read.");
            if (current.Identity != before.Identity || current.Length != before.Length)
                throw Unknown("The control file changed before its bounded contents were read.");

            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = InvokeNative(
                    "read bounded control file",
                    () => UnixNative.ReadAt(platform, fd, bytes, offset));
                if (read == 0)
                    throw Unknown("The control file ended before its observed length.");
                offset += read;
            }
        }

        var after = InspectHandle(file);
        if (after.Kind != PhysicalStoreEntryKind.RegularFile ||
            after.LinkCount != 1 ||
            after.Identity != before.Identity ||
            after.Length != before.Length)
        {
            throw Unknown("The control file changed while its bounded contents were read.");
        }

        return bytes;
    }

    /// <inheritdoc />
    public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
    {
        var platform = RequireSupportedPlatform();
        ArgumentNullException.ThrowIfNull(file);
        file.ClaimInitialWrite(_providerToken);

        using var scopedHandle = file.AcquireScopedSafeHandle(_providerToken);
        var fd = GetFileDescriptor(scopedHandle);
        var before = ToEntryInfo(InvokeNative("inspect new control file before writing", () => UnixNative.StatHandle(platform, fd)));
        RequireControlFile(before, "The new control file is linked or not a regular file.");
        if (before.Length != 0)
            throw Unknown("The new control file already contains data.");

        var data = contents.ToArray();
        var offset = 0;
        while (offset < data.Length)
        {
            var written = InvokeNative(
                "write new control file",
                () => UnixNative.Write(platform, fd, data, offset));
            if (written == 0)
                throw Unknown("The control file write made no progress.");
            offset += written;
        }

        InvokeNative("flush new control file", () => UnixNative.Flush(platform, fd));
        var after = ToEntryInfo(InvokeNative("recheck new control file after writing", () => UnixNative.StatHandle(platform, fd)));
        if (after.Kind != PhysicalStoreEntryKind.RegularFile ||
            after.LinkCount != 1 ||
            after.Identity != before.Identity ||
            after.Length != data.Length)
        {
            throw Unknown("The new control file changed while it was written.");
        }
    }

    /// <inheritdoc />
    public ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
    {
        var platform = RequireSupportedPlatform();
        ArgumentNullException.ThrowIfNull(file);
        var reservation = file.TryReserveLock(_providerToken);
        if (reservation == PhysicalStoreLockReservationResult.Busy)
            return ValueTask.FromResult<IAsyncDisposable?>(null);
        if (reservation == PhysicalStoreLockReservationResult.Poisoned)
            throw Unknown("A previous native lock release failed; this file handle can no longer be used for locking.");

        PhysicalStoreSafeHandleLease? handleLease = null;
        var reservationTransferred = false;
        try
        {
            RequireControlFile(InspectHandle(file), "The lock file is linked or not a regular file.");
            handleLease = file.AcquireScopedSafeHandle(_providerToken);
            var fd = GetFileDescriptor(handleLease);
            var current = ToEntryInfo(InvokeNative("recheck lock file before locking", () => UnixNative.StatHandle(platform, fd)));
            RequireControlFile(current, "The lock file changed before its lock was acquired.");
            var lockResult = InvokeNative("try nonblocking exclusive file lock", () => UnixNative.TryLock(platform, fd));
            if (lockResult.Status == UnixLockStatus.Busy)
                return ValueTask.FromResult<IAsyncDisposable?>(null);
            if (lockResult.Status == UnixLockStatus.Failure)
                throw NativeFailure("try nonblocking exclusive file lock", lockResult.Error);

            var owner = new UnixFileLockOwner(_providerToken, file, handleLease, fd, platform);
            handleLease = null;
            reservationTransferred = true;
            return ValueTask.FromResult<IAsyncDisposable?>(owner);
        }
        finally
        {
            handleLease?.Dispose();
            if (!reservationTransferred)
                file.ReleaseLockReservation(_providerToken);
        }
    }

    private static UnixPlatform RequireSupportedPlatform()
        => UnixNative.GetPlatform() ?? throw Refusal(
            PackageStoreAdmissionReason.UnsupportedFilesystem,
            "The Unix physical store adapter supports only Darwin arm64 and Linux x64/arm64.");

    private static void ValidateName(string name)
    {
        PhysicalStoreNames.ValidateSingleComponent(name);
        try
        {
            _ = StrictUtf8.GetByteCount(name);
        }
        catch (EncoderFallbackException exception)
        {
            throw Unknown("A child name is not valid UTF-8.", exception);
        }
    }

    private static void RequireControlFile(PhysicalStoreEntryInfo? info, string message)
    {
        if (info is null ||
            info.Kind != PhysicalStoreEntryKind.RegularFile ||
            info.LinkCount != 1)
        {
            throw Unknown(message);
        }
    }

    private PhysicalStoreDirectoryHandle OwnDirectory(int fd)
    {
        var safeHandle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        try
        {
            return new PhysicalStoreDirectoryHandle(_providerToken, safeHandle);
        }
        catch
        {
            safeHandle.Dispose();
            throw;
        }
    }

    private PhysicalStoreFileHandle OwnFile(int fd, bool createdExclusive)
    {
        var safeHandle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        try
        {
            return new PhysicalStoreFileHandle(_providerToken, safeHandle, createdExclusive);
        }
        catch
        {
            safeHandle.Dispose();
            throw;
        }
    }

    private static int GetFileDescriptor(PhysicalStoreSafeHandleLease lease)
        => checked((int)lease.DangerousHandle.ToInt64());

    private static PhysicalStoreEntryInfo ToEntryInfo(UnixMetadata metadata)
    {
        if (metadata.Length < 0)
            throw Refusal(PackageStoreAdmissionReason.UnsupportedFilesystem, "The Unix provider returned a negative file size.");
        if (metadata.Inode == 0)
            throw Refusal(PackageStoreAdmissionReason.UnsupportedFilesystem, "The Unix provider returned an unusable zero inode identity.");

        var identity = new PhysicalFileIdentity(
            metadata.Platform == UnixPlatform.Darwin ? "darwin" : "linux",
            metadata.Device,
            metadata.Inode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var type = metadata.Mode & UnixNative.FileTypeMask;
        var kind = type switch
        {
            UnixNative.FileTypeDirectory => PhysicalStoreEntryKind.Directory,
            UnixNative.FileTypeRegular => PhysicalStoreEntryKind.RegularFile,
            UnixNative.FileTypeSymbolicLink => PhysicalStoreEntryKind.SymbolicLink,
            _ => PhysicalStoreEntryKind.Other
        };

        return new PhysicalStoreEntryInfo(kind, identity, metadata.LinkCount, metadata.Length);
    }

    private static T InvokeNative<T>(string operation, Func<T> call)
    {
        try
        {
            return call();
        }
        catch (PackageStoreAdmissionException)
        {
            throw;
        }
        catch (UnixNativeCallException exception) when (exception.Unsupported)
        {
            throw Refusal(
                PackageStoreAdmissionReason.UnsupportedFilesystem,
                $"The Unix provider cannot perform '{operation}' with the metadata or locking support returned by this filesystem.",
                exception);
        }
        catch (UnixNativeCallException exception)
        {
            throw NativeFailure(operation, exception.Error, exception);
        }
        catch (IOException exception)
        {
            throw Unknown($"The Unix provider could not {operation}.", exception);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException or PlatformNotSupportedException)
        {
            throw Refusal(
                PackageStoreAdmissionReason.UnsupportedFilesystem,
                $"The Unix provider cannot perform '{operation}' on this runtime or filesystem.",
                exception);
        }
    }

    private static PackageStoreAdmissionException NativeFailure(string operation, int error, Exception? innerException = null)
    {
        var reason = UnixNative.IsUnsupportedError(error)
            ? PackageStoreAdmissionReason.UnsupportedFilesystem
            : PackageStoreAdmissionReason.UnknownAuthority;
        return Refusal(reason, $"The Unix provider could not {operation} (native error {error}).", innerException);
    }

    private static PackageStoreAdmissionException Unknown(string message, Exception? innerException = null)
        => Refusal(PackageStoreAdmissionReason.UnknownAuthority, message, innerException);

    private static PackageStoreAdmissionException Refusal(
        PackageStoreAdmissionReason reason,
        string message,
        Exception? innerException = null)
        => new(reason, message, innerException: innerException);

    private sealed class UnixFileLockOwner(
        object providerToken,
        PhysicalStoreFileHandle file,
        PhysicalStoreSafeHandleLease handleLease,
        int fileDescriptor,
        UnixPlatform platform) : IAsyncDisposable
    {
        private readonly object _gate = new();
        private Task? _disposeTask;

        public ValueTask DisposeAsync()
        {
            TaskCompletionSource completion;
            Task disposal;
            lock (_gate)
            {
                if (_disposeTask is not null)
                    return new ValueTask(_disposeTask);

                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = completion.Task;
                disposal = _disposeTask;
            }

            Release(completion);
            return new ValueTask(disposal);
        }

        private void Release(TaskCompletionSource completion)
        {
            List<Exception>? failures = null;
            var unlocked = false;
            try
            {
                InvokeNative("release exclusive file lock", () => UnixNative.Unlock(platform, fileDescriptor));
                unlocked = true;
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            if (unlocked)
            {
                try
                {
                    file.ReleaseLockReservation(providerToken);
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }
            else
            {
                try
                {
                    file.MarkLockReleaseFailed(providerToken);
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }

            try
            {
                handleLease.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            if (failures is null)
                completion.TrySetResult();
            else if (failures.Count == 1)
                completion.TrySetException(failures[0]);
            else
                completion.TrySetException(new AggregateException("The Unix lock owner could not release all owned resources.", failures));
        }
    }
}
