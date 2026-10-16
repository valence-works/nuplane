using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Unix;

namespace Nuplane.Store.Coordination;

internal sealed partial class UnixPhysicalStoreFileSystem
{
    public PhysicalStoreDirectoryCandidateSample SamplePackageCandidateNoFollow(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity? expectedIdentity,
        IDisposable? identityPin)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ValidateName(singleName);
        if (identityPin is not null)
            throw Refusal(PackageStoreAdmissionReason.UnsupportedFilesystem,
                "The Unix package-candidate sampler received a foreign native identity pin.");
        var platform = RequireSupportedPlatform();
        var names = (IPhysicalStoreNameFileSystem)this;

        var parentBefore = InspectHandle(parent);
        if (parentBefore.Kind != PhysicalStoreEntryKind.Directory)
            throw Unknown("A package candidate requires a held parent directory.");
        var profileBefore = names.ObserveDirectoryNameSemantics(parent);

        int descriptor;
        using (var parentLease = parent.AcquireScopedSafeHandle(_providerToken))
        {
            try
            {
                // This is deliberately the first open of the candidate file: O_NOFOLLOW and O_NONBLOCK
                // are applied by the parent-relative native open before any child-name inspection.
                descriptor = UnixNative.OpenFileAt(platform, GetFileDescriptor(parentLease), singleName, FileAccess.Read);
            }
            catch (UnixNativeCallException exception) when (exception.Error == 2)
            {
                var absent = InspectChildNoFollow(parent, singleName);
                var parentAfterAbsence = InspectHandle(parent);
                var profileAfterAbsence = names.ObserveDirectoryNameSemantics(parent);
                if (absent is not null || parentAfterAbsence.Kind != PhysicalStoreEntryKind.Directory ||
                    parentAfterAbsence.Identity != parentBefore.Identity || profileAfterAbsence != profileBefore ||
                    expectedIdentity is not null)
                {
                    throw Unknown("A package candidate disappeared or changed during native absence observation.");
                }

                return new PhysicalStoreDirectoryCandidateSample(PhysicalStoreDirectoryCandidateKind.Missing);
            }
            catch (UnixNativeCallException exception)
            {
                throw NativeFailure("open a package candidate relative to its held directory", exception.Error, exception);
            }
        }

        using var file = OwnFile(descriptor, createdExclusive: false);
        using var fileLease = file.AcquireScopedSafeHandle(_providerToken);
        var fileDescriptor = GetFileDescriptor(fileLease);
        var opened = ToEntryInfo(InvokeNative("inspect an opened package candidate", () => UnixNative.StatHandle(platform, fileDescriptor)));
        if (opened.Kind == PhysicalStoreEntryKind.Directory)
        {
            var namedDirectory = InspectChildNoFollow(parent, singleName);
            var parentAfterDirectory = InspectHandle(parent);
            var profileAfterDirectory = names.ObserveDirectoryNameSemantics(parent);
            if (expectedIdentity is not null || namedDirectory is not { Kind: PhysicalStoreEntryKind.Directory } ||
                namedDirectory.Identity != opened.Identity || parentAfterDirectory.Kind != PhysicalStoreEntryKind.Directory ||
                parentAfterDirectory.Identity != parentBefore.Identity || profileAfterDirectory != profileBefore)
            {
                throw Unknown("A package-suffix directory candidate changed during native classification.");
            }

            return new PhysicalStoreDirectoryCandidateSample(PhysicalStoreDirectoryCandidateKind.Directory);
        }

        RequireControlFile(opened, "A package-suffix candidate must be a regular single-link file without a final alias.");
        if (expectedIdentity is not null && opened.Identity != expectedIdentity)
            throw Unknown("A package candidate was replaced after its first stable identity was observed.");

        var lockResult = InvokeNative("try a nonblocking shared package-candidate lock",
            () => UnixNative.TrySharedReadLock(platform, fileDescriptor));
        if (lockResult.Status == UnixLockStatus.Failure)
            throw NativeFailure("try a nonblocking shared package-candidate lock", lockResult.Error);

        var acquired = lockResult.Status == UnixLockStatus.Acquired;
        try
        {
            var canonical = names.ObserveCanonicalFileNameNoFollow(parent, singleName, opened.Identity);
            var named = InspectChildNoFollow(parent, canonical.Basename);
            var fileAfter = ToEntryInfo(InvokeNative("recheck an opened package candidate", () => UnixNative.StatHandle(platform, fileDescriptor)));
            var parentAfter = InspectHandle(parent);
            var profileAfter = names.ObserveDirectoryNameSemantics(parent);
            if (!string.Equals(canonical.Basename, singleName, StringComparison.Ordinal) ||
                canonical.ParentIdentity != parentBefore.Identity || canonical.Semantics != profileBefore ||
                canonical.FileIdentity != opened.Identity ||
                named is not { Kind: PhysicalStoreEntryKind.RegularFile, LinkCount: 1 } ||
                named.Identity != opened.Identity || fileAfter.Kind != PhysicalStoreEntryKind.RegularFile ||
                fileAfter.LinkCount != 1 || fileAfter.Identity != opened.Identity || parentAfter.Kind != PhysicalStoreEntryKind.Directory ||
                parentAfter.Identity != parentBefore.Identity || profileAfter != profileBefore)
            {
                throw Unknown("A package candidate, basename, parent, or native name profile changed during sampling.");
            }

            if (!acquired)
                return new PhysicalStoreDirectoryCandidateSample(
                    PhysicalStoreDirectoryCandidateKind.Busy,
                    RegularFileMetadataVerified: true);

            // A writer may finish after the first stat and before we acquire the shared flock.
            // Report the post-lock length that was replayed above instead of that stale pre-lock observation.
            return new PhysicalStoreDirectoryCandidateSample(
                PhysicalStoreDirectoryCandidateKind.RegularFile, opened.Identity, fileAfter.Length);
        }
        finally
        {
            if (acquired)
                InvokeNative("release a shared package-candidate lock", () => UnixNative.Unlock(platform, fileDescriptor));
        }
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
        if (identityPin is not null)
            throw Refusal(PackageStoreAdmissionReason.UnsupportedFilesystem,
                "The Unix package-candidate sampler received a foreign native identity pin.");

        var parentBefore = InspectHandle(parent);
        if (parentBefore.Kind != PhysicalStoreEntryKind.Directory)
            throw Unknown("A sampled package candidate no longer has its held parent directory.");
        var profileBefore = ((IPhysicalStoreNameFileSystem)this).ObserveDirectoryNameSemantics(parent);
        var named = InspectChildNoFollow(parent, singleName);
        if (named is not { Kind: PhysicalStoreEntryKind.RegularFile, LinkCount: 1 } || named.Identity != expectedIdentity)
            throw Unknown("A sampled package candidate's exact native identity or type changed.");

        var canonical = ((IPhysicalStoreNameFileSystem)this)
            .ObserveCanonicalFileNameNoFollow(parent, singleName, expectedIdentity);
        var parentAfter = InspectHandle(parent);
        var profileAfter = ((IPhysicalStoreNameFileSystem)this).ObserveDirectoryNameSemantics(parent);
        if (!string.Equals(canonical.Basename, singleName, StringComparison.Ordinal) ||
            canonical.ParentIdentity != parentBefore.Identity || canonical.FileIdentity != expectedIdentity ||
            canonical.Semantics != profileBefore || parentAfter.Kind != PhysicalStoreEntryKind.Directory ||
            parentAfter.Identity != parentBefore.Identity || profileAfter != profileBefore)
        {
            throw Unknown("A sampled package candidate's parent, exact basename, or native lookup profile changed.");
        }
    }
}
