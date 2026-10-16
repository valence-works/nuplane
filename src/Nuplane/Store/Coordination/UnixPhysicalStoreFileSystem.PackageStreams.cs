using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Unix;

namespace Nuplane.Store.Coordination;

internal sealed partial class UnixPhysicalStoreFileSystem : IPhysicalStorePackageStreamFileSystem
{
    /// <inheritdoc />
    public Stream OpenPackageArchiveReadStream(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalStoreFileHandle file,
        PhysicalStoreEntryInfo expectedParent,
        PhysicalStoreEntryInfo expectedFile,
        long maximumBytes)
    {
        var platform = RequireSupportedPlatform();
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(expectedParent);
        ArgumentNullException.ThrowIfNull(expectedFile);
        ValidateName(singleName);
        if (maximumBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        RequireExpectedPackageParent(expectedParent);
        RequireExpectedPackageFile(expectedFile);
        if (expectedFile.Length > maximumBytes)
            throw Unknown("The package archive exceeds the requested read quota.");

        PhysicalStoreSafeHandleLease? parentLease = null;
        PhysicalStoreSafeHandleLease? fileLease = null;
        try
        {
            parentLease = parent.AcquireScopedSafeHandle(_providerToken);
            fileLease = file.AcquireScopedSafeHandle(_providerToken);
            var parentFd = GetFileDescriptor(parentLease);
            var fileFd = GetFileDescriptor(fileLease);
            var expectedNameProfile = ObservePackageNameProfile(platform, parentFd);
            var expectedCanonicalBasename = ObservePackageCanonicalBasename(platform, parentFd, fileFd);
            RequireExactPackageBasename(singleName, expectedCanonicalBasename);

            void Validate(long requiredLength)
                => ValidatePackageFileBinding(
                    platform,
                    parentFd,
                    fileFd,
                    singleName,
                    expectedParent,
                    expectedFile.Identity,
                    requiredLength,
                    expectedNameProfile,
                    expectedCanonicalBasename);

            Validate(expectedFile.Length);
            var stream = new BoundedPhysicalStoreFileStream(
                parentLease,
                fileLease,
                maximumBytes,
                expectedFile.Length,
                writable: false,
                validateBinding: Validate,
                readAt: (position, buffer, offset, count) => InvokeNative(
                    "read bounded package archive",
                    () => UnixNative.ReadPackageFileAt(platform, fileFd, buffer, offset, count, position)),
                writeAt: static (_, _, _, _) => throw new NotSupportedException(),
                flush: static () => { });
            parentLease = null;
            fileLease = null;
            return stream;
        }
        finally
        {
            fileLease?.Dispose();
            parentLease?.Dispose();
        }
    }

    /// <inheritdoc />
    public Stream CreatePackageFileWriteStream(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalStoreFileHandle file,
        PhysicalStoreEntryInfo expectedParent,
        long maximumBytes)
    {
        var platform = RequireSupportedPlatform();
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(expectedParent);
        ValidateName(singleName);
        if (maximumBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        RequireExpectedPackageParent(expectedParent);

        PhysicalStoreSafeHandleLease? parentLease = null;
        PhysicalStoreSafeHandleLease? fileLease = null;
        try
        {
            parentLease = parent.AcquireScopedSafeHandle(_providerToken);
            fileLease = file.AcquireScopedSafeHandle(_providerToken);
            var parentFd = GetFileDescriptor(parentLease);
            var fileFd = GetFileDescriptor(fileLease);
            var fileInfo = ToEntryInfo(InvokeNative("inspect exclusively created package file", () => UnixNative.StatHandle(platform, fileFd)));
            RequireExpectedPackageFile(fileInfo);
            if (fileInfo.Length != 0)
                throw Unknown("An exclusively created package file already contains data.");
            var expectedNameProfile = ObservePackageNameProfile(platform, parentFd);
            var expectedCanonicalBasename = ObservePackageCanonicalBasename(platform, parentFd, fileFd);
            RequireExactPackageBasename(singleName, expectedCanonicalBasename);

            void Validate(long requiredLength)
                => ValidatePackageFileBinding(
                    platform,
                    parentFd,
                    fileFd,
                    singleName,
                    expectedParent,
                    fileInfo.Identity,
                    requiredLength,
                    expectedNameProfile,
                    expectedCanonicalBasename);

            Validate(0);
            file.ClaimInitialWrite(_providerToken);
            var stream = new BoundedPhysicalStoreFileStream(
                parentLease,
                fileLease,
                maximumBytes,
                readLength: 0,
                writable: true,
                validateBinding: Validate,
                readAt: static (_, _, _, _) => throw new NotSupportedException(),
                writeAt: (position, buffer, offset, count) => InvokeNative(
                    "write bounded package file",
                    () => UnixNative.WritePackageFileAt(platform, fileFd, buffer, offset, count, position)),
                flush: () => InvokeNative("flush bounded package file", () => UnixNative.Flush(platform, fileFd)));
            parentLease = null;
            fileLease = null;
            return stream;
        }
        finally
        {
            fileLease?.Dispose();
            parentLease?.Dispose();
        }
    }

    private void ValidatePackageFileBinding(
        UnixPlatform platform,
        int parentFd,
        int fileFd,
        string singleName,
        PhysicalStoreEntryInfo expectedParent,
        PhysicalFileIdentity expectedFileIdentity,
        long expectedLength,
        UnixNameProfile expectedNameProfile,
        string expectedCanonicalBasename)
    {
        var profileBefore = ObservePackageNameProfile(platform, parentFd);
        var canonicalBefore = ObservePackageCanonicalBasename(platform, parentFd, fileFd);
        var parent = ToEntryInfo(InvokeNative("revalidate package parent handle", () => UnixNative.StatHandle(platform, parentFd)));
        var file = ToEntryInfo(InvokeNative("revalidate package file handle", () => UnixNative.StatHandle(platform, fileFd)));
        var named = InvokeNative("revalidate package child name", () => UnixNative.StatAt(platform, parentFd, singleName));
        if (named.Status == UnixStatResultStatus.Absent)
            throw Unknown("The package child name disappeared while its stream was open.");
        if (named.Status == UnixStatResultStatus.Failure)
            throw NativeFailure("revalidate package child name", named.Error);

        var child = ToEntryInfo(named.Metadata);
        var canonicalAfter = ObservePackageCanonicalBasename(platform, parentFd, fileFd);
        var profileAfter = ObservePackageNameProfile(platform, parentFd);
        if (parent.Kind != PhysicalStoreEntryKind.Directory || parent.Identity != expectedParent.Identity)
            throw Unknown("The package parent handle no longer matches the observed directory identity.");
        RequirePackageFile(file, expectedFileIdentity, expectedLength);
        RequirePackageFile(child, expectedFileIdentity, expectedLength);
        if (file.Identity != child.Identity ||
            !string.Equals(parent.Identity.VolumeOrDeviceId, file.Identity.VolumeOrDeviceId, StringComparison.Ordinal) ||
            profileBefore != expectedNameProfile || profileAfter != expectedNameProfile ||
            !string.Equals(canonicalBefore, expectedCanonicalBasename, StringComparison.Ordinal) ||
            !string.Equals(canonicalAfter, expectedCanonicalBasename, StringComparison.Ordinal) ||
            !string.Equals(canonicalBefore, singleName, StringComparison.Ordinal) ||
            !string.Equals(canonicalAfter, singleName, StringComparison.Ordinal))
        {
            throw Unknown("The package file no longer matches its exact canonical child name and native lookup profile on the held parent volume.");
        }
    }

    private static UnixNameProfile ObservePackageNameProfile(UnixPlatform platform, int parentFd)
        => InvokeNative("observe held package parent name profile", () => UnixNative.GetNameProfile(platform, parentFd));

    private static string ObservePackageCanonicalBasename(UnixPlatform platform, int parentFd, int fileFd)
    {
        var bytes = InvokeNative(
            "observe held package child canonical basename",
            () => UnixNative.FindEntryName(platform, parentFd, fileFd, MaximumCanonicalNameBytes));
        try
        {
            var basename = StrictNameUtf8.GetString(bytes);
            ValidateName(basename);
            return basename;
        }
        catch (Exception exception) when (exception is System.Text.DecoderFallbackException or ArgumentException)
        {
            throw Unknown("The held package file has no valid canonical UTF-8 child basename.", exception);
        }
    }

    private static void RequireExactPackageBasename(string requestedName, string canonicalBasename)
    {
        if (!string.Equals(requestedName, canonicalBasename, StringComparison.Ordinal))
            throw Unknown("The requested package child component is not its exact canonical directory spelling.");
    }

    private static void RequireExpectedPackageFile(PhysicalStoreEntryInfo info)
    {
        if (info.Kind != PhysicalStoreEntryKind.RegularFile || info.LinkCount != 1)
            throw Unknown("Package archive streams require a single-link regular file.");
    }

    private static void RequireExpectedPackageParent(PhysicalStoreEntryInfo info)
    {
        if (info.Kind != PhysicalStoreEntryKind.Directory)
            throw Unknown("Package stream authority requires an observed directory parent.");
    }

    private static void RequirePackageFile(PhysicalStoreEntryInfo info, PhysicalFileIdentity identity, long expectedLength)
    {
        RequireExpectedPackageFile(info);
        if (info.Identity != identity || info.Length != expectedLength)
            throw Unknown("The package file identity or length changed while its bounded stream was open.");
    }
}
