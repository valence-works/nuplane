using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Acquires the physical-root lock before member locks in a deterministic order.</summary>
/// <remarks>
/// This owner provides cooperative lock ordering, not root authority or membership completeness. Callers of
/// <see cref="AcquireAsync"/> must already hold and validate the control directory. The bootstrap callback
/// resolves member metadata under the root lock; it must not read member payloads.
/// </remarks>
internal sealed partial class PhysicalStoreLock
{
    private const string RootLockName = "root.lock";
    private const string MemberLockDomain = "nuplane-member-lock-v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16LittleEndian = new(false, false, true);

    private readonly IPhysicalStoreFileSystem _fileSystem;
    private readonly IPhysicalStoreNameFileSystem _nameFileSystem;

    internal PhysicalStoreLock(IPhysicalStoreFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
        _nameFileSystem = fileSystem as IPhysicalStoreNameFileSystem ?? throw Refusal(
            PackageStoreAdmissionReason.UnsupportedFilesystem,
            "The filesystem provider cannot validate exact lock-file spelling.");
    }

    /// <summary>Gets the deterministic lock-file name for one canonical state slot.</summary>
    internal static string GetMemberLockName(StateSlotIdentity slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendField(hash, Encoding.ASCII.GetBytes(MemberLockDomain));
            AppendField(hash, StrictUtf8.GetBytes(slot.ParentIdentity.Provider));
            AppendField(hash, StrictUtf8.GetBytes(slot.ParentIdentity.VolumeOrDeviceId));
            AppendField(hash, StrictUtf8.GetBytes(slot.ParentIdentity.FileId));
            AppendField(hash, StrictUtf8.GetBytes(slot.NameSemantics.ProfileId));
            AppendByte(hash, (byte)slot.NameSemantics.Encoding);
            AppendByte(hash, slot.NameSemantics.CaseSensitive ? (byte)1 : (byte)0);
            AppendByte(hash, slot.NameSemantics.NormalizationInsensitive ? (byte)1 : (byte)0);
            AppendField(hash, GetCanonicalNameBytes(slot));

            return $"member-{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}.lock";
        }
        catch (EncoderFallbackException exception)
        {
            throw Refusal(
                PackageStoreAdmissionReason.UnknownAuthority,
                "The state-slot lock identity contains text outside its declared encoding.",
                exception);
        }
    }

    /// <summary>Acquires the existing root lock, then existing member locks in stable tuple order.</summary>
    /// <exception cref="PackageStoreAdmissionException">A lock file is absent, ambiguous, unsupported, or busy.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled while acquiring the lock set.</exception>
    internal async Task<IAsyncDisposable> AcquireAsync(
        PhysicalStoreDirectoryHandle controlDirectory,
        IReadOnlyList<StateSlotIdentity> stateSlots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controlDirectory);
        ArgumentNullException.ThrowIfNull(stateSlots);
        cancellationToken.ThrowIfCancellationRequested();

        var orderedSlots = ValidateAndOrderSlots(stateSlots);
        return await AcquireCoreAsync(controlDirectory, orderedSlots, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Acquires the root lock, prepares member slots under it, then creates and acquires their locks.</summary>
    /// <param name="controlDirectory">The held control directory containing <c>root.lock</c>.</param>
    /// <param name="prepareSlotsUnderRoot">A metadata-only callback that resolves the complete member set while the root lock is held.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An owner for the root lock and all sorted member locks.</returns>
    /// <remarks>The callback runs once after root-lock acquisition and must not read member payloads.</remarks>
    /// <exception cref="PackageStoreAdmissionException">A lock file is unsafe, unsupported, or busy.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled while preparing or acquiring the lock set.</exception>
    internal async Task<IAsyncDisposable> AcquireBootstrapAsync(
        PhysicalStoreDirectoryHandle controlDirectory,
        Func<CancellationToken, Task<IReadOnlyList<StateSlotIdentity>>> prepareSlotsUnderRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controlDirectory);
        ArgumentNullException.ThrowIfNull(prepareSlotsUnderRoot);
        cancellationToken.ThrowIfCancellationRequested();

        return await AcquireCoreAsync(controlDirectory, null, prepareSlotsUnderRoot, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IAsyncDisposable> AcquireCoreAsync(
        PhysicalStoreDirectoryHandle controlDirectory,
        StateSlotIdentity[]? orderedSlots,
        Func<CancellationToken, Task<IReadOnlyList<StateSlotIdentity>>>? prepareSlotsUnderRoot,
        CancellationToken cancellationToken)
    {
        var controlInfo = _fileSystem.InspectHandle(controlDirectory);
        if (controlInfo.Kind != PhysicalStoreEntryKind.Directory)
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority, "The held control location is not a directory.");

        var heldLocks = new List<HeldFileLock>((orderedSlots?.Length ?? 0) + 1);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rootLock = await AcquireNamedLockAsync(controlDirectory, controlInfo.Identity, RootLockName, cancellationToken)
                .ConfigureAwait(false);
            heldLocks.Add(rootLock.Lock);
            var lockDirectorySemantics = rootLock.NameSemantics;
            VerifyStillCanonical(controlDirectory, controlInfo.Identity, RootLockName, rootLock.Lock, lockDirectorySemantics);

            if (prepareSlotsUnderRoot is not null)
            {
                var preparedSlots = await prepareSlotsUnderRoot(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                VerifyRootAndDirectoryProfile(
                    controlDirectory,
                    controlInfo.Identity,
                    rootLock.Lock,
                    lockDirectorySemantics);

                orderedSlots = ValidateAndOrderSlots(preparedSlots);
                CreateMissingMemberLockFiles(controlDirectory, orderedSlots, cancellationToken);
            }

            if (orderedSlots is null)
                throw new InvalidOperationException("A lock acquisition requires a prepared member-slot set.");

            foreach (var slot in orderedSlots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lockName = GetMemberLockName(slot);
                var memberLock = await AcquireNamedLockAsync(controlDirectory, controlInfo.Identity, lockName, cancellationToken)
                    .ConfigureAwait(false);
                heldLocks.Add(memberLock.Lock);
                VerifyStillCanonical(controlDirectory, controlInfo.Identity, lockName, memberLock.Lock, lockDirectorySemantics);
                if (memberLock.NameSemantics != lockDirectorySemantics)
                    throw Refusal(PackageStoreAdmissionReason.UnknownAuthority, "The control-directory name profile changed during lock acquisition.");
            }

            if (prepareSlotsUnderRoot is not null)
            {
                VerifyRootAndDirectoryProfile(
                    controlDirectory,
                    controlInfo.Identity,
                    heldLocks[0],
                    lockDirectorySemantics);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new LockSetOwner(heldLocks.ToArray());
        }
        catch (Exception acquisitionError)
        {
            var cleanupErrors = await DisposeLocksInReverseCoreAsync(heldLocks).ConfigureAwait(false);
            if (cleanupErrors.Count > 0)
            {
                throw new AggregateException(
                    "Lock acquisition failed and one or more previously acquired locks could not be released.",
                    new[] { acquisitionError }.Concat(cleanupErrors));
            }

            throw;
        }
    }

    private void CreateMissingMemberLockFiles(
        PhysicalStoreDirectoryHandle controlDirectory,
        IReadOnlyList<StateSlotIdentity> orderedSlots,
        CancellationToken cancellationToken)
    {
        foreach (var slot in orderedSlots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lockName = GetMemberLockName(slot);
            if (_fileSystem.InspectChildNoFollow(controlDirectory, lockName) is not null)
                continue;

            using (var created = _fileSystem.CreateFileExclusiveAt(controlDirectory, lockName))
                _fileSystem.WriteNewControlFile(created, ReadOnlyMemory<byte>.Empty);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async Task<(HeldFileLock Lock, PhysicalStoreNameSemantics NameSemantics)> AcquireNamedLockAsync(
        PhysicalStoreDirectoryHandle controlDirectory,
        PhysicalFileIdentity expectedParentIdentity,
        string singleName,
        CancellationToken cancellationToken)
    {
        PhysicalStoreFileHandle? file = null;
        IAsyncDisposable? nativeLock = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = _fileSystem.InspectChildNoFollow(controlDirectory, singleName);
            RequireLockFile(before, $"The required lock file '{singleName}' is absent or unsafe.");
            var canonical = _nameFileSystem.ObserveCanonicalFileNameNoFollow(controlDirectory, singleName, before!.Identity);
            VerifyCanonicalObservation(canonical, expectedParentIdentity, before.Identity, singleName);

            file = _fileSystem.OpenFileChildNoFollow(controlDirectory, singleName, FileAccess.ReadWrite);
            var opened = _fileSystem.InspectHandle(file);
            RequireLockFile(opened, $"The lock file '{singleName}' is not one regular single-link file.");
            if (opened.Identity != before.Identity)
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority, $"The lock file '{singleName}' changed while being opened.");

            var beforeLock = _fileSystem.InspectChildNoFollow(controlDirectory, singleName);
            RequireLockFile(beforeLock, $"The lock file '{singleName}' changed before its lock was acquired.");
            if (beforeLock!.Identity != opened.Identity)
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority, $"The lock-file name '{singleName}' no longer identifies the opened file.");

            cancellationToken.ThrowIfCancellationRequested();
            nativeLock = await _fileSystem.TryAcquireExclusiveLock(file).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (nativeLock is null)
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority, $"The required lock file '{singleName}' is busy.");

            var acquired = new HeldFileLock(file, nativeLock, opened.Identity);
            file = null;
            nativeLock = null;
            return (acquired, canonical.Semantics);
        }
        catch (Exception acquisitionError)
        {
            var cleanupErrors = new List<Exception>(2);
            if (nativeLock is not null)
            {
                try
                {
                    await nativeLock.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupErrors.Add(exception);
                }
            }

            if (file is not null)
            {
                try
                {
                    file.Dispose();
                }
                catch (Exception exception)
                {
                    cleanupErrors.Add(exception);
                }
            }

            if (cleanupErrors.Count > 0)
            {
                throw new AggregateException(
                    $"Lock-file acquisition failed for '{singleName}' and cleanup also failed.",
                    new[] { acquisitionError }.Concat(cleanupErrors));
            }

            throw;
        }
    }

    private void VerifyStillCanonical(
        PhysicalStoreDirectoryHandle controlDirectory,
        PhysicalFileIdentity expectedParentIdentity,
        string singleName,
        HeldFileLock heldLock,
        PhysicalStoreNameSemantics expectedSemantics)
    {
        var parent = _fileSystem.InspectHandle(controlDirectory);
        if (parent.Kind != PhysicalStoreEntryKind.Directory || parent.Identity != expectedParentIdentity)
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority, "The held control directory changed during lock acquisition.");

        var opened = _fileSystem.InspectHandle(heldLock.File);
        RequireLockFile(opened, $"The lock file '{singleName}' changed while its lock was acquired.");
        var named = _fileSystem.InspectChildNoFollow(controlDirectory, singleName);
        RequireLockFile(named, $"The lock-file name '{singleName}' changed while its lock was acquired.");
        if (named!.Identity != opened.Identity || opened.Identity != heldLock.Identity)
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority, $"The lock-file name '{singleName}' no longer identifies its locked file.");

        var canonical = _nameFileSystem.ObserveCanonicalFileNameNoFollow(controlDirectory, singleName, opened.Identity);
        VerifyCanonicalObservation(canonical, expectedParentIdentity, opened.Identity, singleName);
        if (canonical.Semantics != expectedSemantics)
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority, "The control-directory name profile changed during lock acquisition.");
    }

    private void VerifyRootAndDirectoryProfile(
        PhysicalStoreDirectoryHandle controlDirectory,
        PhysicalFileIdentity expectedParentIdentity,
        HeldFileLock rootLock,
        PhysicalStoreNameSemantics expectedSemantics)
    {
        VerifyStillCanonical(controlDirectory, expectedParentIdentity, RootLockName, rootLock, expectedSemantics);
        var observedSemantics = _nameFileSystem.ObserveDirectoryNameSemantics(controlDirectory);
        if (observedSemantics != expectedSemantics)
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority, "The control-directory name profile changed while preparing member slots.");
    }

    private static StateSlotIdentity[] ValidateAndOrderSlots(IReadOnlyList<StateSlotIdentity> stateSlots)
    {
        ArgumentNullException.ThrowIfNull(stateSlots);
        var slots = new StateSlotIdentity[stateSlots.Count];
        var seen = new HashSet<StateSlotIdentity>();
        for (var index = 0; index < slots.Length; index++)
        {
            var slot = stateSlots[index];
            if (slot is null)
                throw new ArgumentException("The state-slot list cannot contain null entries.", nameof(stateSlots));
            if (!seen.Add(slot))
                throw new ArgumentException("The state-slot list cannot contain duplicate canonical slots.", nameof(stateSlots));
            _ = GetMemberLockName(slot);
            slots[index] = slot;
        }

        Array.Sort(slots, StateSlotComparer.Instance);
        return slots;
    }

    private static byte[] GetCanonicalNameBytes(StateSlotIdentity slot)
        => slot.NameSemantics.Encoding switch
        {
            PhysicalStoreNameEncoding.Utf8 => StrictUtf8.GetBytes(slot.CanonicalBasename),
            PhysicalStoreNameEncoding.Utf16LittleEndian => StrictUtf16LittleEndian.GetBytes(slot.CanonicalBasename),
            _ => throw Refusal(PackageStoreAdmissionReason.UnknownAuthority, "The state slot declares an unsupported name encoding.")
        };

    private static void AppendField(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private static void AppendByte(IncrementalHash hash, byte value)
    {
        Span<byte> field = stackalloc byte[1];
        field[0] = value;
        hash.AppendData(field);
    }

    private static void VerifyCanonicalObservation(
        PhysicalStoreCanonicalName canonical,
        PhysicalFileIdentity expectedParentIdentity,
        PhysicalFileIdentity expectedFileIdentity,
        string requestedName)
    {
        if (canonical.ParentIdentity != expectedParentIdentity ||
            canonical.FileIdentity != expectedFileIdentity ||
            !string.Equals(canonical.Basename, requestedName, StringComparison.Ordinal))
        {
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority, $"The lock-file name '{requestedName}' is not its exact canonical entry.");
        }
    }

    private static void RequireLockFile(PhysicalStoreEntryInfo? info, string message)
    {
        if (info is null || info.Kind != PhysicalStoreEntryKind.RegularFile || info.LinkCount != 1)
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority, message);
    }

    private static async Task<List<Exception>> DisposeLocksInReverseCoreAsync(IReadOnlyList<HeldFileLock> heldLocks)
    {
        var errors = new List<Exception>();
        for (var index = heldLocks.Count - 1; index >= 0; index--)
        {
            try
            {
                await heldLocks[index].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }

        return errors;
    }

    private static PackageStoreAdmissionException Refusal(
        PackageStoreAdmissionReason reason,
        string message,
        Exception? innerException = null)
        => new(reason, message, innerException: innerException);

    internal sealed class HeldFileLock(
        PhysicalStoreFileHandle file,
        IAsyncDisposable nativeLock,
        PhysicalFileIdentity identity) : IAsyncDisposable
    {
        private readonly object _gate = new();
        private Task? _disposeTask;

        internal PhysicalStoreFileHandle File { get; } = file;
        internal PhysicalFileIdentity Identity { get; } = identity;

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                _disposeTask ??= DisposeCoreAsync();
                return new ValueTask(_disposeTask);
            }
        }

        private async Task DisposeCoreAsync()
        {
            List<Exception>? errors = null;
            try
            {
                await nativeLock.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (errors ??= []).Add(exception);
            }

            try
            {
                File.Dispose();
            }
            catch (Exception exception)
            {
                (errors ??= []).Add(exception);
            }

            ThrowCleanupErrors(errors, "A native file lock or its handle could not be released.");
        }
    }

    private sealed class LockSetOwner(HeldFileLock[] heldLocks) : IAsyncDisposable
    {
        private readonly object _gate = new();
        private Task? _disposeTask;

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                _disposeTask ??= DisposeCoreAsync();
                return new ValueTask(_disposeTask);
            }
        }

        private async Task DisposeCoreAsync()
        {
            var errors = await DisposeLocksInReverseCoreAsync(heldLocks).ConfigureAwait(false);
            ThrowCleanupErrors(errors, "One or more physical store locks could not be released.");
        }
    }

    private sealed class StateSlotComparer : IComparer<StateSlotIdentity>
    {
        internal static StateSlotComparer Instance { get; } = new();

        public int Compare(StateSlotIdentity? left, StateSlotIdentity? right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            if (left is null)
                return -1;
            if (right is null)
                return 1;

            var comparison = StringComparer.Ordinal.Compare(left.ParentIdentity.Provider, right.ParentIdentity.Provider);
            if (comparison != 0)
                return comparison;
            comparison = StringComparer.Ordinal.Compare(left.ParentIdentity.VolumeOrDeviceId, right.ParentIdentity.VolumeOrDeviceId);
            if (comparison != 0)
                return comparison;
            comparison = StringComparer.Ordinal.Compare(left.ParentIdentity.FileId, right.ParentIdentity.FileId);
            if (comparison != 0)
                return comparison;
            comparison = StringComparer.Ordinal.Compare(left.NameSemantics.ProfileId, right.NameSemantics.ProfileId);
            if (comparison != 0)
                return comparison;
            comparison = left.NameSemantics.Encoding.CompareTo(right.NameSemantics.Encoding);
            if (comparison != 0)
                return comparison;
            comparison = left.NameSemantics.CaseSensitive.CompareTo(right.NameSemantics.CaseSensitive);
            if (comparison != 0)
                return comparison;
            comparison = left.NameSemantics.NormalizationInsensitive.CompareTo(right.NameSemantics.NormalizationInsensitive);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left.CanonicalBasename, right.CanonicalBasename);
        }
    }

    private static void ThrowCleanupErrors(IReadOnlyList<Exception>? errors, string message)
    {
        if (errors is null || errors.Count == 0)
            return;
        if (errors.Count == 1)
            ExceptionDispatchInfo.Capture(errors[0]).Throw();
        throw new AggregateException(message, errors);
    }
}
