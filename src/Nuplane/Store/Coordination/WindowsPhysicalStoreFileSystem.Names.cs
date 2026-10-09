using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Windows;

namespace Nuplane.Store.Coordination;

internal sealed partial class WindowsPhysicalStoreFileSystem
{
    private const string NameProfileId = "windows-ntfs-name-v1";

    /// <inheritdoc />
    public PhysicalStoreCanonicalName ObserveCanonicalDirectoryNameNoFollow(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedDirectoryIdentity)
        => ObserveCanonicalNameNoFollow(
            parent,
            singleName,
            expectedDirectoryIdentity,
            (directory, name) => OpenDirectoryChildNoFollow(directory, name),
            RequireExpectedCanonicalDirectory,
            "directory");

    /// <inheritdoc />
    public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedFileIdentity)
        => ObserveCanonicalNameNoFollow(
            parent,
            singleName,
            expectedFileIdentity,
            (directory, name) => OpenFileChildNoFollow(directory, name, FileAccess.Read),
            RequireExpectedCanonicalFile,
            "file");

    private PhysicalStoreCanonicalName ObserveCanonicalNameNoFollow(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedIdentity,
        Func<PhysicalStoreDirectoryHandle, string, PhysicalStoreHandle> openChild,
        Action<PhysicalStoreEntryInfo, PhysicalFileIdentity> requireExpected,
        string entryKind)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        ValidateName(singleName);
        RequireSupportedPlatform();

        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var parentState = GetState(parent);
        var parentBefore = QueryEntry(parentLease.DangerousHandle, "inspect the held parent before observing a canonical name");
        RequireKind(parentBefore, PhysicalStoreEntryKind.Directory, "The canonical-name parent is not a directory.");
        if (parentBefore.Identity != parentState.Identity)
            throw Unknown("The canonical-name parent identity changed before observation.");

        var semanticsBefore = ObserveNameSemantics(parentLease.DangerousHandle);
        using var original = openChild(parent, singleName);
        var entryBefore = InspectHandle(original);
        requireExpected(entryBefore, expectedIdentity);
        RequireSameVolume(parentBefore.Identity, entryBefore.Identity);

        string normalizedPath;
        using (var originalLease = original.AcquireScopedSafeHandle(_providerToken))
        {
            try
            {
                normalizedPath = WindowsNative.GetNormalizedVolumePath(originalLease.DangerousHandle);
            }
            catch (WindowsNativeCallException exception)
            {
                throw NativeFailure($"observe the canonical {entryKind} spelling", exception);
            }
        }

        var separator = normalizedPath.LastIndexOf('\\');
        if (separator < 0 || separator == normalizedPath.Length - 1)
            throw Unknown($"The normalized {entryKind} path did not provide a leaf-name candidate.");

        var basename = normalizedPath[(separator + 1)..];
        try
        {
            PhysicalStoreCanonicalName.ValidateBasename(basename, semanticsBefore);
        }
        catch (ArgumentException exception)
        {
            throw Unknown($"The normalized {entryKind} path returned an invalid canonical basename.", exception);
        }

        using var canonical = openChild(parent, basename);
        var canonicalInfo = InspectHandle(canonical);
        using var suppliedAfter = openChild(parent, singleName);
        var suppliedInfo = InspectHandle(suppliedAfter);
        var originalAfter = InspectHandle(original);
        var parentAfter = QueryEntry(parentLease.DangerousHandle, "recheck the held parent after observing a canonical name");
        var semanticsAfter = ObserveNameSemantics(parentLease.DangerousHandle);
        requireExpected(canonicalInfo, expectedIdentity);
        requireExpected(suppliedInfo, expectedIdentity);
        requireExpected(originalAfter, expectedIdentity);
        RequireSameVolume(parentBefore.Identity, canonicalInfo.Identity);
        RequireSameVolume(parentBefore.Identity, suppliedInfo.Identity);

        if (parentAfter.Kind != PhysicalStoreEntryKind.Directory || parentAfter.Identity != parentBefore.Identity ||
            semanticsAfter != semanticsBefore)
        {
            throw Unknown($"The {entryKind}, parent, or native name profile changed during canonical-name observation.");
        }

        return new PhysicalStoreCanonicalName(parentBefore.Identity, expectedIdentity, basename, semanticsBefore);
    }

    private static void RequireExpectedCanonicalFile(PhysicalStoreEntryInfo entry, PhysicalFileIdentity expectedIdentity)
    {
        RequireControlFile(entry, "Canonical-name observation requires a regular single-link file.");
        if (entry.Identity != expectedIdentity)
            throw Unknown("The supplied component does not resolve to the expected file identity.");
    }

    private static void RequireExpectedCanonicalDirectory(PhysicalStoreEntryInfo entry, PhysicalFileIdentity expectedIdentity)
    {
        _ = PhysicalStoreDirectoryPublicationChecks.RequireExpectedDirectory(entry, expectedIdentity);
    }

    private static PhysicalStoreNameSemantics ObserveNameSemantics(IntPtr directoryHandle)
    {
        try
        {
            return new PhysicalStoreNameSemantics(
                NameProfileId,
                PhysicalStoreNameEncoding.Utf16LittleEndian,
                WindowsNative.QueryDirectoryCaseSensitive(directoryHandle),
                normalizationInsensitive: false);
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("query the held directory's native name profile", exception);
        }
    }
}
