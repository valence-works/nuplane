using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Unix;
using Nuplane.Store.Coordination.PhysicalFiles.Windows;

namespace Nuplane.Store.Coordination;

/// <summary>Acquires a supplemental native lock for one exact persistent state slot.</summary>
/// <remarks>
/// This lock serializes cooperating state-slot writers. It does not establish root membership or replace any
/// root-local member lock. The caller must resolve and retain the authoritative parent before acquisition.
/// </remarks>
internal sealed class PhysicalStoreStateSlotWriteGuard
{
    internal const string ControlDirectoryName = ".nuplane-state-slot-controls";
    internal const string GuardLeafName = "guard.lock";
    internal const string GroupMarkerLeafName = "group.binding";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16LittleEndian = new(false, false, true);

    private readonly IPhysicalStoreFileSystem _fileSystem;
    private readonly IPhysicalStoreNameFileSystem _nameFileSystem;
    private readonly IPhysicalStoreDirectoryNameFileSystem _directoryNameFileSystem;

    internal PhysicalStoreStateSlotWriteGuard(IPhysicalStoreFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
        _nameFileSystem = fileSystem as IPhysicalStoreNameFileSystem ?? throw Refusal(
            PackageStoreAdmissionReason.UnsupportedFilesystem,
            "The filesystem provider cannot validate exact state-slot control-file names.");
        _directoryNameFileSystem = fileSystem as IPhysicalStoreDirectoryNameFileSystem ?? throw Refusal(
            PackageStoreAdmissionReason.UnsupportedFilesystem,
            "The filesystem provider cannot validate native control-directory names.");
    }

    /// <summary>Gets the canonical digest which binds every exact field of a state-slot identity.</summary>
    internal static string GetSlotDigest(StateSlotIdentity slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendField(hash, Encoding.ASCII.GetBytes("nuplane-state-slot-write-guard-v1"));
            AppendField(hash, StrictUtf8.GetBytes(slot.ParentIdentity.Provider));
            AppendField(hash, StrictUtf8.GetBytes(slot.ParentIdentity.VolumeOrDeviceId));
            AppendField(hash, StrictUtf8.GetBytes(slot.ParentIdentity.FileId));
            AppendField(hash, StrictUtf8.GetBytes(slot.NameSemantics.ProfileId));
            AppendByte(hash, (byte)slot.NameSemantics.Encoding);
            AppendByte(hash, slot.NameSemantics.CaseSensitive ? (byte)1 : (byte)0);
            AppendByte(hash, slot.NameSemantics.NormalizationInsensitive ? (byte)1 : (byte)0);
            AppendField(hash, GetCanonicalBasenameBytes(slot));
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (EncoderFallbackException exception)
        {
            throw Refusal(
                PackageStoreAdmissionReason.UnknownAuthority,
                "The state-slot identity contains text outside its declared native encoding.",
                exception);
        }
    }

    /// <summary>Acquires the stable leaf lock under the native control directory for the held exact parent.</summary>
    /// <exception cref="PackageStoreAdmissionException">The slot, control directories, provider, or lock is unsafe or unavailable.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled before ownership is returned.</exception>
    internal async Task<PhysicalStoreStateSlotWriteLease> AcquireAsync(
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(slot);
        cancellationToken.ThrowIfCancellationRequested();

        var names = new SlotControlNames(GuardLeafName, GroupMarkerLeafName, GetSlotDigest(slot));
        SlotControlDirectories? directories = OpenControlDirectories(parent, slot, cancellationToken);

        PhysicalStoreFileHandle? file = null;
        IAsyncDisposable? nativeLock = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureStableGuardFile(directories, slot, names, cancellationToken);
            var before = _fileSystem.InspectChildNoFollow(directories.SlotDirectory, names.GuardName);
            RequireEmptySingleLinkFile(before, "The state-slot guard is absent or unsafe.");
            VerifyCanonicalLeafName(directories, slot, names.GuardName, before!.Identity);

            file = _fileSystem.OpenFileChildNoFollow(directories.SlotDirectory, names.GuardName, FileAccess.ReadWrite);
            var opened = _fileSystem.InspectHandle(file);
            RequireEmptySingleLinkFile(opened, "The opened state-slot guard is not one empty regular file.");
            if (opened.Identity != before.Identity)
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                    "The state-slot guard changed between name inspection and open.");

            var beforeLock = _fileSystem.InspectChildNoFollow(directories.SlotDirectory, names.GuardName);
            RequireEmptySingleLinkFile(beforeLock, "The state-slot guard changed before lock acquisition.");
            if (beforeLock!.Identity != opened.Identity)
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                    "The state-slot guard name no longer identifies the opened file.");
            VerifyCanonicalLeafName(directories, slot, names.GuardName, opened.Identity);

            cancellationToken.ThrowIfCancellationRequested();
            nativeLock = await _fileSystem.TryAcquireExclusiveLock(file).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (nativeLock is null)
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority, "The state-slot guard is busy.");

            VerifyLockedGuard(directories, slot, names, file, opened.Identity);
            cancellationToken.ThrowIfCancellationRequested();

            var owner = new PhysicalStoreStateSlotWriteLease(
                this, _fileSystem, _nameFileSystem, slot, names,
                directories, file, nativeLock, opened.Identity);
            directories = null;
            file = null;
            nativeLock = null;
            return owner;
        }
        catch (Exception acquisitionError)
        {
            var cleanupErrors = await DisposeAcquisitionResourcesAsync(nativeLock, file, directories).ConfigureAwait(false);
            if (cleanupErrors.Count > 0)
            {
                throw new AggregateException(
                    "State-slot guard acquisition failed and cleanup also failed.",
                    new[] { acquisitionError }.Concat(cleanupErrors));
            }

            throw;
        }
    }

    private void EnsureStableGuardFile(
        SlotControlDirectories directories,
        StateSlotIdentity slot,
        SlotControlNames names,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerifyControlDirectories(directories, slot);
        var observed = _fileSystem.InspectChildNoFollow(directories.SlotDirectory, names.GuardName);
        if (observed is not null)
        {
            RequireEmptySingleLinkFile(observed, "The state-slot guard name is occupied by an unsafe or non-empty entry.");
            VerifyCanonicalLeafName(directories, slot, names.GuardName, observed.Identity);
            return;
        }

        try
        {
            using var created = _fileSystem.CreateFileExclusiveAt(directories.SlotDirectory, names.GuardName);
            var createdInfo = _fileSystem.InspectHandle(created);
            RequireEmptySingleLinkFile(createdInfo, "The newly created state-slot guard is unsafe.");
            VerifyCanonicalLeafName(directories, slot, names.GuardName, createdInfo.Identity);
            VerifyControlDirectories(directories, slot);
            _fileSystem.WriteNewControlFile(created, ReadOnlyMemory<byte>.Empty);
            cancellationToken.ThrowIfCancellationRequested();
            var afterWrite = _fileSystem.InspectHandle(created);
            RequireEmptySingleLinkFile(afterWrite, "The stable state-slot guard is not empty.");
            if (afterWrite.Identity != createdInfo.Identity)
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                    "The state-slot guard changed while its stable empty file was established.");
            VerifyCanonicalLeafName(directories, slot, names.GuardName, createdInfo.Identity);
            VerifyControlDirectories(directories, slot);
        }
        catch (Exception exception) when (IsExclusiveCreateCollision(exception))
        {
            // Adopt only a positively reobserved, exact canonical zero-content object created by a contender.
            var raced = _fileSystem.InspectChildNoFollow(directories.SlotDirectory, names.GuardName);
            if (raced is null)
                ExceptionDispatchInfo.Capture(exception).Throw();
            RequireEmptySingleLinkFile(raced, "A racing state-slot guard creation left an unsafe entry.");
            VerifyCanonicalLeafName(directories, slot, names.GuardName, raced!.Identity);
            VerifyControlDirectories(directories, slot);
        }
    }

    private void VerifyLockedGuard(
        SlotControlDirectories directories,
        StateSlotIdentity slot,
        SlotControlNames names,
        PhysicalStoreFileHandle file,
        PhysicalFileIdentity expectedIdentity)
    {
        VerifyControlDirectories(directories, slot);
        var held = _fileSystem.InspectHandle(file);
        RequireEmptySingleLinkFile(held, "The held state-slot guard changed after lock acquisition.");
        var named = _fileSystem.InspectChildNoFollow(directories.SlotDirectory, names.GuardName);
        RequireEmptySingleLinkFile(named, "The state-slot guard name changed after lock acquisition.");
        if (held.Identity != expectedIdentity || named!.Identity != expectedIdentity)
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                "The state-slot guard name no longer identifies the exclusively locked file.");
        VerifyCanonicalLeafName(directories, slot, names.GuardName, expectedIdentity);
        VerifyControlDirectories(directories, slot);
    }

    private void VerifyControlDirectories(SlotControlDirectories directories, StateSlotIdentity slot)
    {
        var parentInfo = _fileSystem.InspectHandle(directories.Parent);
        if (parentInfo.Kind != PhysicalStoreEntryKind.Directory || parentInfo.Identity != slot.ParentIdentity)
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                "The held state-slot parent differs from the identity selected for the slot.");
        var semantics = _nameFileSystem.ObserveDirectoryNameSemantics(directories.Parent);
        if (semantics != slot.NameSemantics)
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                "The held state-slot parent no longer has the selected native name profile.");

        var rootEntry = _fileSystem.InspectChildNoFollow(directories.Parent, ControlDirectoryName);
        RequireDirectoryIdentity(rootEntry, directories.RootIdentity,
            "The state-slot control namespace no longer identifies the held directory.");
        var rootCanonical = _directoryNameFileSystem.ObserveCanonicalDirectoryNameNoFollow(
            directories.Parent, ControlDirectoryName, directories.RootIdentity);
        if (rootCanonical.ParentIdentity != slot.ParentIdentity || rootCanonical.Semantics != slot.NameSemantics ||
            !string.Equals(rootCanonical.Basename, ControlDirectoryName, StringComparison.Ordinal))
        {
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                "The state-slot control namespace is not its exact canonical parent entry.");
        }
        RequireDirectoryIdentity(_fileSystem.InspectHandle(directories.RootDirectory), directories.RootIdentity,
            "The held state-slot control namespace changed identity.");
        if (_nameFileSystem.ObserveDirectoryNameSemantics(directories.RootDirectory) != slot.NameSemantics)
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                "The state-slot control namespace has a different native name profile from its parent.");

        var slotEntry = _fileSystem.InspectChildNoFollow(directories.RootDirectory, slot.CanonicalBasename);
        RequireDirectoryIdentity(slotEntry, directories.SlotDirectoryIdentity,
            "The state-slot control child no longer identifies the held slot directory.");
        var slotCanonical = _directoryNameFileSystem.ObserveCanonicalDirectoryNameNoFollow(
            directories.RootDirectory, slot.CanonicalBasename, directories.SlotDirectoryIdentity);
        if (slotCanonical.ParentIdentity != directories.RootIdentity || slotCanonical.Semantics != slot.NameSemantics ||
            !string.Equals(slotCanonical.Basename, directories.SlotCanonicalBasename, StringComparison.Ordinal))
        {
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                "The native-equivalent state-slot control child changed its canonical entry or lookup profile.");
        }
        RequireDirectoryIdentity(_fileSystem.InspectHandle(directories.SlotDirectory), directories.SlotDirectoryIdentity,
            "The held state-slot control child changed identity.");
        if (_nameFileSystem.ObserveDirectoryNameSemantics(directories.SlotDirectory) != slot.NameSemantics)
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                "The state-slot child has a different native name profile from its parent.");

        var stateNameEntry = _fileSystem.InspectChildNoFollow(directories.Parent, slot.CanonicalBasename);
        if (stateNameEntry?.Identity == directories.RootIdentity)
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                "The requested state basename aliases the reserved state-slot control namespace.");
    }

    private void VerifyCanonicalLeafName(
        SlotControlDirectories directories,
        StateSlotIdentity slot,
        string name,
        PhysicalFileIdentity expectedFileIdentity)
    {
        var canonical = _nameFileSystem.ObserveCanonicalFileNameNoFollow(
            directories.SlotDirectory, name, expectedFileIdentity);
        if (canonical.ParentIdentity != directories.SlotDirectoryIdentity ||
            canonical.FileIdentity != expectedFileIdentity ||
            canonical.Semantics != slot.NameSemantics ||
            !string.Equals(canonical.Basename, name, StringComparison.Ordinal))
        {
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                $"The state-slot control name '{name}' is not its exact canonical single-link entry.");
        }
    }

    private SlotControlDirectories OpenControlDirectories(
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        CancellationToken cancellationToken)
    {
        PhysicalStoreDirectoryHandle? root = null;
        PhysicalStoreDirectoryHandle? slotDirectory = null;
        try
        {
            RequireSelectedParent(parent, slot);
            root = OpenOrCreateControlDirectory(parent, ControlDirectoryName, slot.NameSemantics,
                requireExactCanonicalName: true, cancellationToken);
            var rootIdentity = _fileSystem.InspectHandle(root).Identity;
            if (_fileSystem.InspectChildNoFollow(parent, slot.CanonicalBasename)?.Identity == rootIdentity)
            {
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                    "The requested state basename aliases the reserved state-slot control namespace.");
            }

            slotDirectory = OpenOrCreateControlDirectory(root, slot.CanonicalBasename, slot.NameSemantics,
                requireExactCanonicalName: false, cancellationToken, out var storedSlotBasename);
            var slotIdentity = _fileSystem.InspectHandle(slotDirectory).Identity;
            var directories = new SlotControlDirectories(
                parent, root, rootIdentity, slotDirectory, slotIdentity, storedSlotBasename);
            VerifyControlDirectories(directories, slot);
            root = null;
            slotDirectory = null;
            return directories;
        }
        catch (Exception acquisitionError)
        {
            var cleanupErrors = DisposeDirectoryHandles(slotDirectory, root);
            if (cleanupErrors.Count > 0)
                throw new AggregateException(
                    "Control-directory acquisition failed and cleanup also failed.",
                    new[] { acquisitionError }.Concat(cleanupErrors));

            ExceptionDispatchInfo.Capture(acquisitionError).Throw();
            throw;
        }
    }

    private PhysicalStoreDirectoryHandle OpenOrCreateControlDirectory(
        PhysicalStoreDirectoryHandle parent,
        string requestedName,
        PhysicalStoreNameSemantics expectedSemantics,
        bool requireExactCanonicalName,
        CancellationToken cancellationToken)
        => OpenOrCreateControlDirectory(parent, requestedName, expectedSemantics,
            requireExactCanonicalName, cancellationToken, out _);

    private PhysicalStoreDirectoryHandle OpenOrCreateControlDirectory(
        PhysicalStoreDirectoryHandle parent,
        string requestedName,
        PhysicalStoreNameSemantics expectedSemantics,
        bool requireExactCanonicalName,
        CancellationToken cancellationToken,
        out string canonicalBasename)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_nameFileSystem.ObserveDirectoryNameSemantics(parent) != expectedSemantics)
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                "A state-slot control directory parent has a different native name profile.");

        PhysicalStoreDirectoryHandle? opened = null;
        try
        {
            var observed = _fileSystem.InspectChildNoFollow(parent, requestedName);
            PhysicalFileIdentity? inspectedIdentity = observed?.Identity;
            if (observed is null)
            {
                try
                {
                    opened = _fileSystem.CreateDirectoryExclusiveAt(parent, requestedName);
                }
                catch (Exception exception) when (IsExclusiveCreateCollision(exception))
                {
                    var raced = _fileSystem.InspectChildNoFollow(parent, requestedName);
                    if (raced is null)
                        ExceptionDispatchInfo.Capture(exception).Throw();
                    observed = raced;
                    inspectedIdentity = raced.Identity;
                }
            }

            if (opened is null)
            {
                if (observed is null || observed.Kind != PhysicalStoreEntryKind.Directory)
                    throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                        "The state-slot control directory name is occupied by an unsafe entry.");
                opened = _fileSystem.OpenDirectoryChildNoFollow(parent, requestedName);
            }

            var openedInfo = _fileSystem.InspectHandle(opened);
            if (openedInfo.Kind != PhysicalStoreEntryKind.Directory)
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                    "The opened state-slot control child is not a directory.");
            if (inspectedIdentity is not null && openedInfo.Identity != inspectedIdentity)
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                    "The state-slot control directory changed between no-follow inspection and open.");
            var named = _fileSystem.InspectChildNoFollow(parent, requestedName);
            RequireDirectoryIdentity(named, openedInfo.Identity,
                "The state-slot control directory changed between inspection and open.");
            var canonical = _directoryNameFileSystem.ObserveCanonicalDirectoryNameNoFollow(
                parent, requestedName, openedInfo.Identity);
            if (canonical.ParentIdentity != _fileSystem.InspectHandle(parent).Identity ||
                canonical.Semantics != expectedSemantics ||
                (requireExactCanonicalName && !string.Equals(canonical.Basename, requestedName, StringComparison.Ordinal)))
            {
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                    "The state-slot control directory is not a canonical entry under the selected native profile.");
            }
            if (_nameFileSystem.ObserveDirectoryNameSemantics(opened) != expectedSemantics)
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                    "A state-slot control directory did not inherit the expected native name profile.");
            var final = _fileSystem.InspectChildNoFollow(parent, requestedName);
            RequireDirectoryIdentity(final, openedInfo.Identity,
                "The state-slot control directory changed during profile observation.");
            canonicalBasename = canonical.Basename;
            var result = opened;
            opened = null;
            return result;
        }
        catch (Exception acquisitionError)
        {
            if (opened is not null)
            {
                try
                {
                    opened.Dispose();
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException(
                        "A state-slot control directory could not be validated or its handle could not be released.",
                        acquisitionError, cleanupError);
                }
            }

            ExceptionDispatchInfo.Capture(acquisitionError).Throw();
            throw;
        }
    }

    private void RequireSelectedParent(PhysicalStoreDirectoryHandle parent, StateSlotIdentity slot)
    {
        var parentInfo = _fileSystem.InspectHandle(parent);
        if (parentInfo.Kind != PhysicalStoreEntryKind.Directory || parentInfo.Identity != slot.ParentIdentity ||
            _nameFileSystem.ObserveDirectoryNameSemantics(parent) != slot.NameSemantics)
        {
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                "The held state-slot parent differs from the selected identity or native name profile.");
        }
    }

    private static PhysicalStoreEntryInfo RequireDirectoryIdentity(
        PhysicalStoreEntryInfo? entry,
        PhysicalFileIdentity expectedIdentity,
        string message)
    {
        if (entry is null || entry.Kind != PhysicalStoreEntryKind.Directory ||
            entry.Identity != expectedIdentity)
        {
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority, message);
        }

        return entry;
    }

    private static byte[] GetCanonicalBasenameBytes(StateSlotIdentity slot)
        => slot.NameSemantics.Encoding switch
        {
            PhysicalStoreNameEncoding.Utf8 => StrictUtf8.GetBytes(slot.CanonicalBasename),
            PhysicalStoreNameEncoding.Utf16LittleEndian => StrictUtf16LittleEndian.GetBytes(slot.CanonicalBasename),
            _ => throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                "The state slot declares an unsupported canonical-name encoding.")
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

    private static void RequireEmptySingleLinkFile(PhysicalStoreEntryInfo? info, string message)
    {
        if (info is null || info.Kind != PhysicalStoreEntryKind.RegularFile || info.LinkCount != 1 || info.Length != 0)
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority, message);
    }

    private static bool IsExclusiveCreateCollision(Exception exception)
        => PhysicalStoreExclusiveCreateCollision.IsCollision(exception);

    private static async Task<List<Exception>> DisposeAcquisitionResourcesAsync(
        IAsyncDisposable? nativeLock,
        PhysicalStoreFileHandle? file,
        SlotControlDirectories? directories)
    {
        var errors = new List<Exception>(4);
        if (nativeLock is not null)
        {
            try
            {
                await nativeLock.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                errors.Add(exception);
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
                errors.Add(exception);
            }
        }

        if (directories is not null)
            errors.AddRange(DisposeDirectoryHandles(directories.SlotDirectory, directories.RootDirectory));

        return errors;
    }

    private static List<Exception> DisposeDirectoryHandles(
        PhysicalStoreDirectoryHandle? slotDirectory,
        PhysicalStoreDirectoryHandle? rootDirectory)
    {
        var errors = new List<Exception>(2);
        if (slotDirectory is not null)
        {
            try
            {
                slotDirectory.Dispose();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }

        if (rootDirectory is not null)
        {
            try
            {
                rootDirectory.Dispose();
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

    internal sealed record SlotControlNames(string GuardName, string GroupMarkerName, string SlotDigest);

    internal sealed class SlotControlDirectories
    {
        internal SlotControlDirectories(
            PhysicalStoreDirectoryHandle parent,
            PhysicalStoreDirectoryHandle rootDirectory,
            PhysicalFileIdentity rootIdentity,
            PhysicalStoreDirectoryHandle slotDirectory,
            PhysicalFileIdentity slotDirectoryIdentity,
            string slotCanonicalBasename)
        {
            Parent = parent;
            RootDirectory = rootDirectory;
            RootIdentity = rootIdentity;
            SlotDirectory = slotDirectory;
            SlotDirectoryIdentity = slotDirectoryIdentity;
            SlotCanonicalBasename = slotCanonicalBasename;
        }

        internal PhysicalStoreDirectoryHandle Parent { get; }
        internal PhysicalStoreDirectoryHandle RootDirectory { get; }
        internal PhysicalFileIdentity RootIdentity { get; }
        internal PhysicalStoreDirectoryHandle SlotDirectory { get; }
        internal PhysicalFileIdentity SlotDirectoryIdentity { get; }
        internal string SlotCanonicalBasename { get; }
    }

    /// <summary>Owns the native state-slot guard and permits only identity-bound marker operations.</summary>
    internal sealed class PhysicalStoreStateSlotWriteLease : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly PhysicalStoreStateSlotWriteGuard _owner;
        private readonly IPhysicalStoreFileSystem _fileSystem;
        private readonly IPhysicalStoreNameFileSystem _nameFileSystem;
        private readonly StateSlotIdentity _slot;
        private readonly SlotControlNames _names;
        private readonly SlotControlDirectories _directories;
        private readonly PhysicalStoreFileHandle _guardFile;
        private readonly IAsyncDisposable _nativeLock;
        private readonly PhysicalFileIdentity _guardIdentity;
        private PhysicalFileIdentity? _markerIdentity;
        private PhysicalStoreGroupBindingMarker? _markerValue;
        private Task? _disposeTask;

        internal PhysicalStoreStateSlotWriteLease(
            PhysicalStoreStateSlotWriteGuard owner,
            IPhysicalStoreFileSystem fileSystem,
            IPhysicalStoreNameFileSystem nameFileSystem,
            StateSlotIdentity slot,
            SlotControlNames names,
            SlotControlDirectories directories,
            PhysicalStoreFileHandle guardFile,
            IAsyncDisposable nativeLock,
            PhysicalFileIdentity guardIdentity)
        {
            _owner = owner;
            _fileSystem = fileSystem;
            _nameFileSystem = nameFileSystem;
            _slot = slot;
            _names = names;
            _directories = directories;
            _guardFile = guardFile;
            _nativeLock = nativeLock;
            _guardIdentity = guardIdentity;
        }

        /// <summary>Refuses if any entry occupies the group marker name, without reading its contents.</summary>
        internal void RequireGroupBindingMarkerAbsent()
        {
            lock (_gate)
            {
                EnsureActive();
                VerifyGuard();
                var marker = _fileSystem.InspectChildNoFollow(_directories.SlotDirectory, _names.GroupMarkerName);
                VerifyGuard();
                if (marker is null && _markerIdentity is not null)
                    throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                        "An already observed permanent group marker disappeared while the guard was owned.");
                if (marker is not null)
                    throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                        "A state-slot group marker is present; a path-only writer cannot write this slot.");
            }
        }

        /// <summary>Reads and validates the immutable group binding marker, if one exists.</summary>
        internal PhysicalStoreGroupBindingMarker? ReadGroupBindingMarker()
        {
            lock (_gate)
            {
                EnsureActive();
                return ReadMarkerObservation()?.Marker;
            }
        }

        /// <summary>Revalidates the live guard and any group marker identity already observed by this owner.</summary>
        internal void Revalidate()
        {
            lock (_gate)
            {
                EnsureActive();
                VerifyGuard();
                if (_markerIdentity is { } markerIdentity)
                {
                    var marker = ReadMarkerObservation();
                    if (marker is null || marker.Identity != markerIdentity)
                        throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                            "The already observed permanent group marker changed while the guard was owned.");
                }
                else if (_fileSystem.InspectChildNoFollow(_directories.SlotDirectory, _names.GroupMarkerName) is not null)
                {
                    throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                        "A group marker appeared while the state-slot guard was owned without being bound by this owner.");
                }
                VerifyGuard();
            }
        }

        /// <summary>Creates or reuses the exact immutable group binding under this held native guard.</summary>
        internal PhysicalStoreGroupBindingMarker EnsureGroupBindingMarker(
            Guid logicalMemberId,
            string participantSetDigest,
            CancellationToken cancellationToken)
        {
            if (logicalMemberId == Guid.Empty)
                throw new ArgumentException("A group marker requires a non-empty logical member identity.", nameof(logicalMemberId));
            ArgumentNullException.ThrowIfNull(participantSetDigest);
            ProtectionDigest.ValidateCanonicalDigest(participantSetDigest);

            lock (_gate)
            {
                EnsureActive();
                cancellationToken.ThrowIfCancellationRequested();
                var expected = PhysicalStoreGroupBindingMarker.Create(_names.SlotDigest, logicalMemberId, participantSetDigest);
                var existing = ReadMarkerObservation();
                if (existing is not null)
                    return RequireMatchingMarker(existing, expected).Marker;

                cancellationToken.ThrowIfCancellationRequested();
                VerifyGuard();
                var bytes = PhysicalStoreGroupBindingMarker.Encode(expected);
                PhysicalStoreFileHandle created;
                try
                {
                    created = _fileSystem.CreateFileExclusiveAt(_directories.SlotDirectory, _names.GroupMarkerName);
                }
                catch (Exception exception) when (IsExclusiveCreateCollision(exception))
                {
                    var raced = ReadMarkerObservation();
                    if (raced is null)
                        ExceptionDispatchInfo.Capture(exception).Throw();
                    return RequireMatchingMarker(raced!, expected).Marker;
                }

                PhysicalFileIdentity createdIdentity;
                using (created)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    VerifyGuard();
                    var before = _fileSystem.InspectHandle(created);
                    RequireMarkerFile(before, "The newly created group marker is not an empty regular single-link file.", requireExpectedLength: 0);
                    VerifyControlFileName(_names.GroupMarkerName, before.Identity);
                    _fileSystem.WriteNewControlFile(created, bytes);
                    createdIdentity = before.Identity;
                    cancellationToken.ThrowIfCancellationRequested();
                    var after = _fileSystem.InspectHandle(created);
                    RequireMarkerFile(after, "The group marker changed while its durable contents were written.", bytes.Length);
                    if (after.Identity != before.Identity)
                        throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                            "The group marker identity changed while its contents were written.");
                    VerifyControlFileName(_names.GroupMarkerName, before.Identity);
                    VerifyGuard();
                }

                cancellationToken.ThrowIfCancellationRequested();
                var reopened = ReadMarkerObservation();
                if (reopened is null || reopened.Identity != createdIdentity)
                {
                    throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                        "The group marker identity or contents changed after its durable initial write.");
                }

                return RequireMatchingMarker(reopened, expected).Marker;
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                _disposeTask ??= DisposeCoreAsync();
                return new ValueTask(_disposeTask);
            }
        }

        private MarkerObservation? ReadMarkerObservation()
        {
            EnsureActive();
            VerifyGuard();
            var before = _fileSystem.InspectChildNoFollow(_directories.SlotDirectory, _names.GroupMarkerName);
            if (before is null)
            {
                if (_markerIdentity is not null)
                    throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                        "An already observed permanent group marker disappeared while the guard was owned.");
                VerifyGuard();
                return null;
            }

            RequireMarkerFile(before, "The group marker is not one bounded regular single-link file.");
            if (_markerIdentity is { } pinnedIdentity && before.Identity != pinnedIdentity)
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                    "The permanent group marker was replaced while the state-slot guard was owned.");
            VerifyControlFileName(_names.GroupMarkerName, before.Identity);
            using var file = _fileSystem.OpenFileChildNoFollow(_directories.SlotDirectory, _names.GroupMarkerName, FileAccess.Read);
            var opened = _fileSystem.InspectHandle(file);
            RequireMarkerFile(opened, "The opened group marker is not one bounded regular single-link file.");
            if (opened.Identity != before.Identity)
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                    "The group marker changed between no-follow inspection and open.");
            VerifyControlFileName(_names.GroupMarkerName, opened.Identity);

            var bytes = _fileSystem.ReadControlFile(file, PhysicalStoreGroupBindingMarker.MaximumBytes);
            var afterRead = _fileSystem.InspectHandle(file);
            RequireMarkerFile(afterRead, "The group marker changed while it was read.");
            if (afterRead.Identity != opened.Identity || afterRead.Length != opened.Length)
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                    "The group marker identity or length changed while it was read.");

            var namedAfter = _fileSystem.InspectChildNoFollow(_directories.SlotDirectory, _names.GroupMarkerName);
            RequireMarkerFile(namedAfter, "The group marker name changed while it was read.");
            if (namedAfter!.Identity != opened.Identity)
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                    "The group marker name no longer identifies the opened marker.");
            VerifyControlFileName(_names.GroupMarkerName, opened.Identity);
            VerifyGuard();

            var marker = PhysicalStoreGroupBindingMarker.Decode(bytes, _names.SlotDigest);
            if (_markerValue is { } pinnedValue && marker != pinnedValue)
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                    "The permanent group marker contents changed while the state-slot guard was owned.");
            _markerIdentity ??= opened.Identity;
            _markerValue ??= marker;
            return new MarkerObservation(marker, opened.Identity);
        }

        private MarkerObservation RequireMatchingMarker(
            MarkerObservation actual,
            PhysicalStoreGroupBindingMarker expected)
        {
            if (actual.Marker != expected)
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                    "The state slot is already bound to a different logical member or participant set.");
            return actual;
        }

        private void VerifyGuard()
        {
            _owner.VerifyControlDirectories(_directories, _slot);
            var held = _fileSystem.InspectHandle(_guardFile);
            RequireEmptySingleLinkFile(held, "The held state-slot guard changed while owned.");
            var named = _fileSystem.InspectChildNoFollow(_directories.SlotDirectory, _names.GuardName);
            RequireEmptySingleLinkFile(named, "The state-slot guard name changed while owned.");
            if (held.Identity != _guardIdentity || named!.Identity != _guardIdentity)
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                    "The state-slot guard name no longer identifies the exclusively locked file.");
            VerifyControlFileName(_names.GuardName, _guardIdentity);
        }

        private void VerifyControlFileName(string name, PhysicalFileIdentity identity)
        {
            var canonical = _nameFileSystem.ObserveCanonicalFileNameNoFollow(_directories.SlotDirectory, name, identity);
            if (canonical.ParentIdentity != _directories.SlotDirectoryIdentity || canonical.FileIdentity != identity ||
                canonical.Semantics != _slot.NameSemantics ||
                !string.Equals(canonical.Basename, name, StringComparison.Ordinal))
            {
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                    $"The state-slot control name '{name}' is not its exact canonical entry.");
            }
        }

        private static void RequireMarkerFile(
            PhysicalStoreEntryInfo? info,
            string message,
            int? requireExpectedLength = null)
        {
            if (info is null || info.Kind != PhysicalStoreEntryKind.RegularFile || info.LinkCount != 1 ||
                info.Length > PhysicalStoreGroupBindingMarker.MaximumBytes ||
                (requireExpectedLength is null && info.Length <= 0) ||
                (requireExpectedLength is { } length && info.Length != length))
            {
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority, message);
            }
        }

        private void EnsureActive()
        {
            if (_disposeTask is not null)
                throw Refusal(PackageStoreAdmissionReason.ExpiredScope,
                    "The state-slot write guard has been disposed.");
        }

        private async Task DisposeCoreAsync()
        {
            List<Exception>? errors = null;
            try
            {
                await _nativeLock.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (errors ??= []).Add(exception);
            }

            try
            {
                _guardFile.Dispose();
            }
            catch (Exception exception)
            {
                (errors ??= []).Add(exception);
            }

            var directoryErrors = DisposeDirectoryHandles(_directories.SlotDirectory, _directories.RootDirectory);
            if (directoryErrors.Count > 0)
                (errors ??= []).AddRange(directoryErrors);

            if (errors is { Count: 1 })
                ExceptionDispatchInfo.Capture(errors[0]).Throw();
            if (errors is { Count: > 1 })
                throw new AggregateException("The state-slot native guard could not be fully released.", errors);
        }

        private sealed record MarkerObservation(PhysicalStoreGroupBindingMarker Marker, PhysicalFileIdentity Identity);
    }
}
