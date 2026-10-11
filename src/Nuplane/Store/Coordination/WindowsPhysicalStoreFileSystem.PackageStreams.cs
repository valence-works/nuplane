using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Windows;

namespace Nuplane.Store.Coordination;

internal sealed partial class WindowsPhysicalStoreFileSystem : IPhysicalStorePackageStreamFileSystem
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
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(expectedParent);
        ArgumentNullException.ThrowIfNull(expectedFile);
        ValidateName(singleName);
        RequireSupportedPlatform();
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
            var parentHandle = parentLease.DangerousHandle;
            var fileHandle = fileLease.DangerousHandle;
            var expectedNameSemantics = ObserveNameSemantics(parentHandle);
            var expectedCanonicalBasename = ObservePackageCanonicalBasename(fileHandle, expectedNameSemantics);
            RequireExactPackageBasename(singleName, expectedCanonicalBasename);

            void Validate(long requiredLength)
                => ValidatePackageFileBinding(
                    parentHandle,
                    fileHandle,
                    singleName,
                    expectedParent,
                    expectedFile.Identity,
                    requiredLength,
                    expectedNameSemantics,
                    expectedCanonicalBasename);

            Validate(expectedFile.Length);
            var stream = new BoundedPhysicalStoreFileStream(
                parentLease,
                fileLease,
                maximumBytes,
                expectedFile.Length,
                writable: false,
                validateBinding: Validate,
                readAt: (position, buffer, offset, count) => InvokePackageNative(
                    "read bounded package archive",
                    () => WindowsNative.ReadPackageFileAt(fileHandle, position, buffer, offset, count)),
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
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(expectedParent);
        ValidateName(singleName);
        RequireSupportedPlatform();
        if (maximumBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        RequireExpectedPackageParent(expectedParent);

        PhysicalStoreSafeHandleLease? parentLease = null;
        PhysicalStoreSafeHandleLease? fileLease = null;
        try
        {
            parentLease = parent.AcquireScopedSafeHandle(_providerToken);
            fileLease = file.AcquireScopedSafeHandle(_providerToken);
            var parentHandle = parentLease.DangerousHandle;
            var fileHandle = fileLease.DangerousHandle;
            var fileInfo = QueryPackageEntry(fileHandle, "inspect exclusively created package file");
            RequireExpectedPackageFile(fileInfo);
            if (fileInfo.Length != 0)
                throw Unknown("An exclusively created package file already contains data.");
            var expectedNameSemantics = ObserveNameSemantics(parentHandle);
            var expectedCanonicalBasename = ObservePackageCanonicalBasename(fileHandle, expectedNameSemantics);
            RequireExactPackageBasename(singleName, expectedCanonicalBasename);

            void Validate(long requiredLength)
                => ValidatePackageFileBinding(
                    parentHandle,
                    fileHandle,
                    singleName,
                    expectedParent,
                    fileInfo.Identity,
                    requiredLength,
                    expectedNameSemantics,
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
                writeAt: (position, buffer, offset, count) => InvokePackageNative(
                    "write bounded package file",
                    () => WindowsNative.WritePackageFileAt(fileHandle, position, buffer, offset, count)),
                flush: () => InvokePackageNative("flush bounded package file", () => WindowsNative.Flush(fileHandle)));
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
        IntPtr parentHandle,
        IntPtr fileHandle,
        string singleName,
        PhysicalStoreEntryInfo expectedParent,
        PhysicalFileIdentity expectedFileIdentity,
        long expectedLength,
        PhysicalStoreNameSemantics expectedNameSemantics,
        string expectedCanonicalBasename)
    {
        try
        {
            var profileBefore = ObserveNameSemantics(parentHandle);
            var canonicalBefore = ObservePackageCanonicalBasename(fileHandle, expectedNameSemantics);
            var parent = QueryPackageEntry(parentHandle, "revalidate package parent handle");
            var file = QueryPackageEntry(fileHandle, "revalidate package file handle");
            using var namedHandle = OpenChildForInspection(parentHandle, singleName);
            if (namedHandle is null)
                throw Unknown("The package child name disappeared while its stream was open.");
            var named = QueryPackageEntry(namedHandle.DangerousGetHandle(), "revalidate package child name");
            var canonicalAfter = ObservePackageCanonicalBasename(fileHandle, expectedNameSemantics);
            var profileAfter = ObserveNameSemantics(parentHandle);

            if (parent.Kind != PhysicalStoreEntryKind.Directory || parent.Identity != expectedParent.Identity)
                throw Unknown("The package parent handle no longer matches the observed directory identity.");
            RequirePackageFile(file, expectedFileIdentity, expectedLength);
            RequirePackageFile(named, expectedFileIdentity, expectedLength);
            RequireSameVolume(parent.Identity, file.Identity);
            RequireSameVolume(parent.Identity, named.Identity);
            if (file.Identity != named.Identity ||
                profileBefore != expectedNameSemantics || profileAfter != expectedNameSemantics ||
                !string.Equals(canonicalBefore, expectedCanonicalBasename, StringComparison.Ordinal) ||
                !string.Equals(canonicalAfter, expectedCanonicalBasename, StringComparison.Ordinal) ||
                !string.Equals(canonicalBefore, singleName, StringComparison.Ordinal) ||
                !string.Equals(canonicalAfter, singleName, StringComparison.Ordinal))
            {
                throw Unknown("The package file no longer matches its exact canonical child name and native lookup profile on the held parent volume.");
            }
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("revalidate bounded package stream identity", exception);
        }
    }

    private string ObservePackageCanonicalBasename(IntPtr fileHandle, PhysicalStoreNameSemantics semantics)
    {
        string normalizedPath;
        try
        {
            normalizedPath = WindowsNative.GetNormalizedVolumePath(fileHandle);
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("observe the held package file's canonical basename", exception);
        }

        var separator = normalizedPath.LastIndexOf('\\');
        if (separator < 0 || separator == normalizedPath.Length - 1)
            throw Unknown("The normalized package path did not provide a canonical basename.");

        var basename = normalizedPath[(separator + 1)..];
        try
        {
            PhysicalStoreCanonicalName.ValidateBasename(basename, semantics);
            ValidateName(basename);
            return basename;
        }
        catch (ArgumentException exception)
        {
            throw Unknown("The normalized package path returned an invalid canonical basename.", exception);
        }
    }

    private static void RequireExactPackageBasename(string requestedName, string canonicalBasename)
    {
        if (!string.Equals(requestedName, canonicalBasename, StringComparison.Ordinal))
            throw Unknown("The requested package child component is not its exact canonical directory spelling.");
    }

    private static PhysicalStoreEntryInfo QueryPackageEntry(IntPtr handle, string operation)
    {
        try
        {
            return ToEntryInfo(WindowsNative.QueryEntry(handle));
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure(operation, exception);
        }
    }

    private static T InvokePackageNative<T>(string operation, Func<T> call)
    {
        try
        {
            return call();
        }
        catch (PackageStoreAdmissionException)
        {
            throw;
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure(operation, exception);
        }
    }

    private static void InvokePackageNative(string operation, Action call)
    {
        try
        {
            call();
        }
        catch (PackageStoreAdmissionException)
        {
            throw;
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure(operation, exception);
        }
    }

    private static void RequireExpectedPackageParent(PhysicalStoreEntryInfo info)
    {
        if (info.Kind != PhysicalStoreEntryKind.Directory)
            throw Unknown("Package stream authority requires an observed directory parent.");
    }

    private static void RequireExpectedPackageFile(PhysicalStoreEntryInfo info)
    {
        if (info.Kind != PhysicalStoreEntryKind.RegularFile || info.LinkCount != 1)
            throw Unknown("Package archive streams require a single-link regular file.");
    }

    private static void RequirePackageFile(PhysicalStoreEntryInfo info, PhysicalFileIdentity identity, long expectedLength)
    {
        RequireExpectedPackageFile(info);
        if (info.Identity != identity || info.Length != expectedLength)
            throw Unknown("The package file identity or length changed while its bounded stream was open.");
    }
}
