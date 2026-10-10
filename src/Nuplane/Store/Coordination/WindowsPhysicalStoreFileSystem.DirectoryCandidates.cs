using Microsoft.Win32.SafeHandles;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Windows;

namespace Nuplane.Store.Coordination;

internal sealed partial class WindowsPhysicalStoreFileSystem
{
    public PhysicalStoreDirectoryCandidateSample SamplePackageCandidateNoFollow(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity? expectedIdentity,
        IDisposable? identityPin)
    {
        WindowsDirectoryCandidateIdentityPin? pin = null;
        if (identityPin is not null)
        {
            pin = identityPin as WindowsDirectoryCandidateIdentityPin
                ?? throw Refusal(PackageStoreAdmissionReason.UnsupportedFilesystem,
                    "The Windows package-candidate sampler received a foreign identity pin.");
            lock (pin.Gate)
            {
                pin.RequireActive(_providerToken);
                if (expectedIdentity != pin.FileIdentity)
                    throw Unknown("The retained Windows package-candidate pin does not match the expected identity.");
                return SamplePackageCandidateCore(parent, singleName, expectedIdentity, pin);
            }
        }

        if (expectedIdentity is not null)
            throw Unknown("A Windows package-candidate retry lost its required native identity pin.");
        return SamplePackageCandidateCore(parent, singleName, expectedIdentity, pin: null);
    }

    public void RevalidatePackageCandidate(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedIdentity,
        IDisposable? identityPin)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        ValidateName(singleName);
        RequireSupportedPlatform();
        var pin = identityPin as WindowsDirectoryCandidateIdentityPin
            ?? throw Refusal(PackageStoreAdmissionReason.UnsupportedFilesystem,
                "A retained Windows package candidate is missing its provider-owned identity pin.");
        lock (pin.Gate)
        {
            pin.RequireActive(_providerToken);
            if (pin.FileIdentity != expectedIdentity || pin.Name != singleName)
                throw Unknown("The retained Windows package-candidate pin no longer matches its exact name and identity.");
            RevalidateWindowsCandidatePin(parent, singleName, pin);
        }
    }

    private PhysicalStoreDirectoryCandidateSample SamplePackageCandidateCore(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity? expectedIdentity,
        WindowsDirectoryCandidateIdentityPin? pin)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ValidateName(singleName);
        RequireSupportedPlatform();

        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var parentHandle = parentLease.DangerousHandle;
        var parentState = GetState(parent);
        var parentBefore = QueryEntry(parentHandle, "inspect the held source directory before sampling a package candidate");
        RequireKind(parentBefore, PhysicalStoreEntryKind.Directory, "A package candidate requires a held parent directory.");
        if (parentBefore.Identity != parentState.Identity)
            throw Unknown("The held source-directory identity changed before candidate sampling.");
        var profileBefore = ObserveNameSemantics(parentHandle);

        if (pin is not null)
        {
            pin.RequireContext(parentBefore.Identity, singleName, profileBefore);
            RevalidateWindowsCandidatePin(parent, singleName, pin);
        }

        SafeFileHandle? opened = null;
        try
        {
            // This is deliberately the first open of the candidate file. The exact share-violation
            // status is the only Windows native outcome reported as retryable Busy.
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
            var absent = InspectChildNoFollow(parent, singleName);
            var parentAfter = QueryEntry(parentHandle, "recheck the held parent after candidate absence");
            var profileAfter = ObserveNameSemantics(parentHandle);
            if (absent is not null || expectedIdentity is not null || pin is not null ||
                parentAfter.Kind != PhysicalStoreEntryKind.Directory || parentAfter.Identity != parentBefore.Identity ||
                profileAfter != profileBefore)
            {
                throw Unknown("A package candidate disappeared or changed during native absence observation.");
            }

            return new PhysicalStoreDirectoryCandidateSample(PhysicalStoreDirectoryCandidateKind.Missing);
        }
        catch (WindowsNativeCallException exception)
            when (exception.NtStatus == WindowsNative.StatusSharingViolation)
        {
            var parentAfter = QueryEntry(parentHandle, "recheck the held parent after a sharing violation");
            var profileAfter = ObserveNameSemantics(parentHandle);
            if (parentAfter.Kind != PhysicalStoreEntryKind.Directory || parentAfter.Identity != parentBefore.Identity ||
                profileAfter != profileBefore)
            {
                throw Unknown("The held source directory changed while a package candidate was busy.");
            }

            if (pin is not null)
            {
                RevalidateWindowsCandidatePin(parent, singleName, pin);
                return new PhysicalStoreDirectoryCandidateSample(
                    PhysicalStoreDirectoryCandidateKind.Busy,
                    pin.FileIdentity,
                    IdentityPin: pin,
                    RegularFileMetadataVerified: true);
            }

            if (expectedIdentity is not null)
                throw Unknown("A busy Windows package candidate cannot be matched to its previously observed identity.");

            _ = VerifyBusyCandidateMetadata(parentHandle, parentBefore.Identity, singleName, profileBefore);
            return new PhysicalStoreDirectoryCandidateSample(
                PhysicalStoreDirectoryCandidateKind.Busy,
                RegularFileMetadataVerified: true);
        }
        catch (WindowsNativeCallException exception)
            when (exception.NtStatus == WindowsNative.StatusFileIsADirectory)
        {
            var entry = InspectChildNoFollow(parent, singleName);
            var parentAfter = QueryEntry(parentHandle, "recheck the held parent after directory classification");
            var profileAfter = ObserveNameSemantics(parentHandle);
            if (expectedIdentity is not null || pin is not null || entry is not { Kind: PhysicalStoreEntryKind.Directory } ||
                parentAfter.Kind != PhysicalStoreEntryKind.Directory || parentAfter.Identity != parentBefore.Identity ||
                profileAfter != profileBefore)
            {
                throw Unknown("A package-suffix directory candidate changed during native classification.");
            }

            return new PhysicalStoreDirectoryCandidateSample(PhysicalStoreDirectoryCandidateKind.Directory);
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("open a package candidate relative to its held directory", exception);
        }

        using (opened)
        {
            var entry = QueryEntry(opened.DangerousGetHandle(), "inspect an opened package candidate");
            RequireControlFile(entry, "A package-suffix candidate must be a regular single-link file without a final alias.");
            RequireSameVolume(parentBefore.Identity, entry.Identity);
            if (expectedIdentity is not null && entry.Identity != expectedIdentity)
                throw Unknown("A package candidate was replaced after its first stable identity was observed.");

            string normalizedPath;
            try
            {
                normalizedPath = WindowsNative.GetNormalizedVolumePath(opened.DangerousGetHandle());
            }
            catch (WindowsNativeCallException exception)
            {
                throw NativeFailure("observe a package candidate's canonical basename", exception);
            }

            var separator = normalizedPath.LastIndexOf('\\');
            if (separator < 0 || separator == normalizedPath.Length - 1)
                throw Unknown("The normalized package candidate path did not provide a basename.");
            var basename = normalizedPath[(separator + 1)..];
            ValidateCandidateBasename(basename, profileBefore);
            var canonical = ((IPhysicalStoreNameFileSystem)this)
                .ObserveCanonicalFileNameNoFollow(parent, basename, entry.Identity);
            var named = InspectChildNoFollow(parent, singleName);
            var after = QueryEntry(opened.DangerousGetHandle(), "recheck an opened package candidate");
            var parentAfterSample = QueryEntry(parentHandle, "recheck the held parent after package candidate sampling");
            var profileAfterSample = ObserveNameSemantics(parentHandle);
            if (!string.Equals(basename, singleName, StringComparison.Ordinal) ||
                canonical.ParentIdentity != parentBefore.Identity || canonical.FileIdentity != entry.Identity ||
                canonical.Semantics != profileBefore || named is not { Kind: PhysicalStoreEntryKind.RegularFile, LinkCount: 1 } ||
                named.Identity != entry.Identity || after.Kind != PhysicalStoreEntryKind.RegularFile || after.LinkCount != 1 ||
                after.Identity != entry.Identity || after.Length != entry.Length ||
                parentAfterSample.Kind != PhysicalStoreEntryKind.Directory || parentAfterSample.Identity != parentBefore.Identity ||
                profileAfterSample != profileBefore)
            {
                throw Unknown("A package candidate, basename, parent, or native name profile changed during sampling.");
            }

            var resultPin = pin;
            if (resultPin is null)
                resultPin = CreateWindowsCandidatePin(
                    parent, parentBefore.Identity, singleName, entry, profileBefore, opened.DangerousGetHandle());

            return new PhysicalStoreDirectoryCandidateSample(
                PhysicalStoreDirectoryCandidateKind.RegularFile, entry.Identity, entry.Length, resultPin);
        }
    }

    private WindowsDirectoryCandidateIdentityPin CreateWindowsCandidatePin(
        PhysicalStoreDirectoryHandle parent,
        PhysicalFileIdentity parentIdentity,
        string singleName,
        PhysicalStoreEntryInfo sampled,
        PhysicalStoreNameSemantics profile,
        IntPtr sampledHandle)
    {
        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        SafeFileHandle? opened = null;
        try
        {
            // Attribute-only access does not participate in Windows data/delete share checks.
            // Retain ReadData without reading bytes so omission of FILE_SHARE_DELETE prevents
            // rename/unlink. ShareRead|ShareWrite still permits writers that share read access.
            opened = WindowsNative.OpenRelative(
                parentLease.DangerousHandle,
                singleName,
                WindowsNative.FileReadData | WindowsNative.FileReadAttributes | WindowsNative.Synchronize,
                WindowsNative.FileOpen,
                WindowsNative.FileNonDirectoryFile,
                shareAccess: WindowsNative.ShareRead | WindowsNative.ShareWrite);
            var openedInfo = QueryEntry(opened.DangerousGetHandle(), "establish a package-candidate identity pin");
            RequireControlFile(openedInfo, "The package-candidate identity pin is not a regular single-link file.");
            if (openedInfo.Identity != sampled.Identity || openedInfo.Length != sampled.Length)
                throw Unknown("The package candidate changed before its native identity pin was established.");

            var sampledBeforeTransfer = QueryEntry(sampledHandle, "replay the first package-candidate sample before pin transfer");
            var sampledPathBeforeTransfer = WindowsNative.GetNormalizedVolumePath(sampledHandle);
            var sampledPathSeparator = sampledPathBeforeTransfer.LastIndexOf('\\');
            if (sampledPathSeparator < 0 || sampledPathSeparator == sampledPathBeforeTransfer.Length - 1)
                throw Unknown("The first package-candidate sample lost its canonical basename before pin transfer.");
            var sampledBasenameBeforeTransfer = sampledPathBeforeTransfer[(sampledPathSeparator + 1)..];
            ValidateCandidateBasename(sampledBasenameBeforeTransfer, profile);

            var canonical = ((IPhysicalStoreNameFileSystem)this)
                .ObserveCanonicalFileNameNoFollow(parent, singleName, sampled.Identity);
            var named = InspectChildNoFollow(parent, singleName);
            var parentAfter = InspectHandle(parent);
            var profileAfter = ((IPhysicalStoreNameFileSystem)this).ObserveDirectoryNameSemantics(parent);
            var pinInfo = QueryEntry(opened.DangerousGetHandle(), "recheck a package-candidate identity pin");
            var sampledAfterTransfer = QueryEntry(sampledHandle, "recheck the first package-candidate sample before pin transfer");
            var sampledPathAfterTransfer = WindowsNative.GetNormalizedVolumePath(sampledHandle);
            var sampledAfterSeparator = sampledPathAfterTransfer.LastIndexOf('\\');
            if (sampledAfterSeparator < 0 || sampledAfterSeparator == sampledPathAfterTransfer.Length - 1)
                throw Unknown("The first package-candidate sample lost its canonical basename during pin transfer.");
            var sampledBasenameAfterTransfer = sampledPathAfterTransfer[(sampledAfterSeparator + 1)..];
            ValidateCandidateBasename(sampledBasenameAfterTransfer, profile);
            if (canonical.ParentIdentity != parentIdentity || canonical.FileIdentity != sampled.Identity ||
                !string.Equals(canonical.Basename, singleName, StringComparison.Ordinal) || canonical.Semantics != profile ||
                named is not { Kind: PhysicalStoreEntryKind.RegularFile, LinkCount: 1 } || named.Identity != sampled.Identity ||
                parentAfter.Kind != PhysicalStoreEntryKind.Directory || parentAfter.Identity != parentIdentity ||
                profileAfter != profile || pinInfo.Kind != PhysicalStoreEntryKind.RegularFile || pinInfo.LinkCount != 1 ||
                pinInfo.Identity != sampled.Identity || pinInfo.Length != sampled.Length ||
                sampledBeforeTransfer.Kind != PhysicalStoreEntryKind.RegularFile || sampledBeforeTransfer.LinkCount != 1 ||
                sampledBeforeTransfer.Identity != sampled.Identity || sampledBeforeTransfer.Length != sampled.Length ||
                !string.Equals(sampledBasenameBeforeTransfer, singleName, StringComparison.Ordinal) ||
                sampledAfterTransfer.Kind != PhysicalStoreEntryKind.RegularFile || sampledAfterTransfer.LinkCount != 1 ||
                sampledAfterTransfer.Identity != sampled.Identity || sampledAfterTransfer.Length != sampled.Length ||
                !string.Equals(sampledBasenameAfterTransfer, singleName, StringComparison.Ordinal) ||
                !string.Equals(sampledPathBeforeTransfer, sampledPathAfterTransfer, StringComparison.Ordinal))
            {
                throw Unknown("The package candidate's exact name or identity changed while its native pin was established.");
            }

            var result = new WindowsDirectoryCandidateIdentityPin(
                _providerToken, opened, parentIdentity, sampled.Identity, singleName, profile);
            opened = null;
            return result;
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("establish a package-candidate identity pin", exception);
        }
        finally
        {
            opened?.Dispose();
        }
    }

    private void RevalidateWindowsCandidatePin(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        WindowsDirectoryCandidateIdentityPin pin)
    {
        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var parentHandle = parentLease.DangerousHandle;
        var parentInfo = QueryEntry(parentHandle, "inspect the held parent while replaying a candidate pin");
        RequireKind(parentInfo, PhysicalStoreEntryKind.Directory, "A candidate identity pin has no held parent directory.");
        var profile = ObserveNameSemantics(parentHandle);
        pin.RequireContext(parentInfo.Identity, singleName, profile);

        var pinnedInfo = QueryEntry(pin.Handle, "replay a package-candidate identity pin");
        RequireControlFile(pinnedInfo, "The retained package-candidate identity pin changed type or link count.");
        if (pinnedInfo.Identity != pin.FileIdentity)
            throw Unknown("The retained package-candidate handle changed its physical identity.");

        // A cooperating writer may hold FILE_WRITE_DATA while sharing read. Keep this replay
        // metadata-only and allow both data access classes so it remains compatible with that
        // writer; the candidate read below is the operation whose ShareRead-only mode reports Busy.
        // The original canonical basename was established before this private pin was retained.
        var named = InspectWindowsCandidateNameForPin(parentHandle, singleName);
        string normalizedPath;
        try
        {
            normalizedPath = WindowsNative.GetNormalizedVolumePath(pin.Handle);
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("replay a pinned package candidate's canonical basename", exception);
        }
        var separator = normalizedPath.LastIndexOf('\\');
        if (separator < 0 || separator == normalizedPath.Length - 1)
            throw Unknown("The pinned package candidate did not provide a canonical basename.");
        var basename = normalizedPath[(separator + 1)..];
        ValidateCandidateBasename(basename, profile);
        var parentAfter = QueryEntry(parentHandle, "recheck the held parent after candidate-pin replay");
        var profileAfter = ObserveNameSemantics(parentHandle);
        var pinAfter = QueryEntry(pin.Handle, "recheck a package-candidate identity pin");
        if (named is not { Kind: PhysicalStoreEntryKind.RegularFile, LinkCount: 1 } || named.Identity != pin.FileIdentity ||
            !string.Equals(basename, singleName, StringComparison.Ordinal) ||
            parentAfter.Kind != PhysicalStoreEntryKind.Directory || parentAfter.Identity != parentInfo.Identity ||
            profileAfter != profile || pinAfter.Kind != PhysicalStoreEntryKind.RegularFile || pinAfter.LinkCount != 1 ||
            pinAfter.Identity != pin.FileIdentity)
        {
            throw Unknown("A retained package candidate's exact name, identity, or native profile changed.");
        }
    }

    private PhysicalStoreEntryInfo? InspectWindowsCandidateNameForPin(IntPtr parent, string singleName)
    {
        SafeFileHandle? opened = null;
        try
        {
            opened = WindowsNative.OpenRelative(
                parent,
                singleName,
                WindowsNative.FileReadAttributes | WindowsNative.Synchronize,
                WindowsNative.FileOpen,
                WindowsNative.FileNonDirectoryFile,
                shareAccess: WindowsNative.ShareRead | WindowsNative.ShareWrite);
            return QueryEntry(opened.DangerousGetHandle(), "inspect a package candidate using its metadata pin share profile");
        }
        catch (WindowsNativeCallException exception) when (exception.NtStatus is { } status && WindowsNative.IsPositiveAbsence(status))
        {
            return null;
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("replay a package candidate relative to its held parent", exception);
        }
        finally
        {
            opened?.Dispose();
        }
    }

    private PhysicalStoreEntryInfo VerifyBusyCandidateMetadata(
        IntPtr parentHandle,
        PhysicalFileIdentity parentIdentity,
        string singleName,
        PhysicalStoreNameSemantics expectedProfile)
    {
        SafeFileHandle? opened = null;
        try
        {
            // A data-read sample can be blocked by a cooperating writer's share mode. A separate
            // metadata-only no-follow open may still positively classify that exact direct child;
            // it does not establish the sampler's retained identity or stability pin.
            opened = WindowsNative.OpenRelative(
                parentHandle,
                singleName,
                WindowsNative.FileReadAttributes | WindowsNative.Synchronize,
                WindowsNative.FileOpen,
                WindowsNative.FileNonDirectoryFile,
                shareAccess: WindowsNative.ShareRead | WindowsNative.ShareWrite);
            var info = QueryEntry(opened.DangerousGetHandle(), "classify a busy package candidate using native metadata");
            RequireControlFile(info, "A busy package candidate is not a regular single-link file.");
            RequireSameVolume(parentIdentity, info.Identity);

            string normalizedPath;
            try
            {
                normalizedPath = WindowsNative.GetNormalizedVolumePath(opened.DangerousGetHandle());
            }
            catch (WindowsNativeCallException exception)
            {
                throw NativeFailure("observe a busy candidate's canonical basename", exception);
            }

            var separator = normalizedPath.LastIndexOf('\\');
            if (separator < 0 || separator == normalizedPath.Length - 1)
                throw Unknown("A busy package candidate has no canonical basename.");
            var basename = normalizedPath[(separator + 1)..];
            ValidateCandidateBasename(basename, expectedProfile);
            if (!string.Equals(basename, singleName, StringComparison.Ordinal))
                throw Unknown("A busy package candidate's canonical basename differs from its enumerated name.");

            var named = InspectWindowsCandidateNameForPin(parentHandle, singleName);
            var after = QueryEntry(opened.DangerousGetHandle(), "recheck busy package-candidate metadata");
            var parentAfter = QueryEntry(parentHandle, "recheck the held parent after busy candidate classification");
            var profileAfter = ObserveNameSemantics(parentHandle);
            if (named is not { Kind: PhysicalStoreEntryKind.RegularFile, LinkCount: 1 } || named.Identity != info.Identity ||
                after.Kind != PhysicalStoreEntryKind.RegularFile || after.LinkCount != 1 || after.Identity != info.Identity ||
                parentAfter.Kind != PhysicalStoreEntryKind.Directory || parentAfter.Identity != parentIdentity ||
                profileAfter != expectedProfile)
            {
                throw Unknown("A busy package candidate, its exact name, or its held parent changed during metadata classification.");
            }

            return after;
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("verify a busy package candidate using native metadata", exception);
        }
        finally
        {
            opened?.Dispose();
        }
    }

    private static void ValidateCandidateBasename(string basename, PhysicalStoreNameSemantics profile)
    {
        try
        {
            PhysicalStoreCanonicalName.ValidateBasename(basename, profile);
        }
        catch (ArgumentException exception)
        {
            throw Unknown("The native package-candidate basename is not a valid canonical component.", exception);
        }
    }

    private sealed class WindowsDirectoryCandidateIdentityPin : IDisposable
    {
        private readonly object _creatorToken;
        private SafeFileHandle? _handle;

        internal WindowsDirectoryCandidateIdentityPin(
            object creatorToken,
            SafeFileHandle handle,
            PhysicalFileIdentity parentIdentity,
            PhysicalFileIdentity fileIdentity,
            string name,
            PhysicalStoreNameSemantics profile)
        {
            _creatorToken = creatorToken;
            _handle = handle;
            ParentIdentity = parentIdentity;
            FileIdentity = fileIdentity;
            Name = name;
            Profile = profile;
        }

        internal object Gate { get; } = new();
        internal IntPtr Handle
        {
            get
            {
                if (_handle is null || _handle.IsClosed || _handle.IsInvalid)
                    throw new ObjectDisposedException(nameof(WindowsDirectoryCandidateIdentityPin));
                return _handle.DangerousGetHandle();
            }
        }
        internal PhysicalFileIdentity ParentIdentity { get; }
        internal PhysicalFileIdentity FileIdentity { get; }
        internal string Name { get; }
        internal PhysicalStoreNameSemantics Profile { get; }

        internal void RequireActive(object creatorToken)
        {
            if (!ReferenceEquals(creatorToken, _creatorToken))
                throw Refusal(PackageStoreAdmissionReason.UnsupportedFilesystem,
                    "A package-candidate identity pin belongs to another provider.");
            if (_handle is null || _handle.IsClosed || _handle.IsInvalid)
                throw Refusal(PackageStoreAdmissionReason.ExpiredScope,
                    "A package-candidate identity pin has expired.");
        }

        internal void RequireContext(
            PhysicalFileIdentity parentIdentity,
            string name,
            PhysicalStoreNameSemantics profile)
        {
            if (ParentIdentity != parentIdentity || !string.Equals(Name, name, StringComparison.Ordinal) || Profile != profile)
                throw Unknown("A package-candidate identity pin belongs to a different parent, basename, or native profile.");
        }

        public void Dispose()
        {
            lock (Gate)
            {
                _handle?.Dispose();
                _handle = null;
            }
        }
    }
}
